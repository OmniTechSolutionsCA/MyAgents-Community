using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MyAgents.Core;
using MyAgents.Core.Tools;

namespace MyAgents.Runtime.Chat;

/// <summary>
/// Creates MyAgents agents backed by any <see cref="IChatClient"/> provider.
/// </summary>
/// <remarks>
/// This Community runtime supports text and multimodal message forwarding, model selection,
/// and framework-managed tool calls with the core approval and observation contracts. It is
/// intentionally in-process and does not include graph persistence, hosted execution, or a
/// provider-specific client.
/// </remarks>
public sealed class ChatClientRuntime : IAgentRuntime
{
    private readonly IChatClient _chatClient;
    private readonly ChatClientRuntimeOptions _options;

    /// <summary>Creates a runtime using the configured chat client and runtime options.</summary>
    public ChatClientRuntime(IChatClient chatClient, IOptions<ChatClientRuntimeOptions> options)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentException("Runtime options are required.", nameof(options));
        if (_options.MaxToolRoundTrips < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxToolRoundTrips must be at least one.");
        if (_options.MaxOutputTokens is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxOutputTokens must be greater than zero when specified.");
    }

    /// <inheritdoc />
    public IAgent CreateAgent(AgentDefinition definition, AgentAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(assembly);

        var modelId = assembly.Capabilities
            .OfType<AgentModelCapability>()
            .LastOrDefault()?.ModelId
            ?? definition.Capabilities?.OfType<AgentModelCapability>().LastOrDefault()?.ModelId
            ?? _options.DefaultModelId;

        return new ChatClientAgent(
            _chatClient,
            modelId,
            assembly.Prompts.Build(),
            assembly.Tools.Build(),
            assembly.ToolObservers.AsReadOnly(),
            _options);
    }

    private sealed class ChatClientAgent(
        IChatClient chatClient,
        string? modelId,
        string? systemPrompt,
        IReadOnlyList<ITool> tools,
        IReadOnlyList<IToolCallObserver> observers,
        ChatClientRuntimeOptions options) : IAgent
    {
        private readonly IReadOnlyList<AITool> _modelTools = tools
            .Select(static tool => (AITool)new ToolDeclaration(tool))
            .ToArray();

        public async ValueTask<AgentTurnResult> InvokeAsync(
            AgentTurnContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var history = BuildMessages(context.Messages);
            var requestOptions = BuildChatOptions();
            var started = Stopwatch.GetTimestamp();
            var toolCallCount = 0;
            long? promptTokens = null;
            long? completionTokens = null;

            for (var round = 0; round <= options.MaxToolRoundTrips; round++)
            {
                var response = await chatClient.GetResponseAsync(history, requestOptions, cancellationToken)
                    .ConfigureAwait(false);
                promptTokens = AddTokenCounts(promptTokens, response.Usage?.InputTokenCount);
                completionTokens = AddTokenCounts(completionTokens, response.Usage?.OutputTokenCount);

                var message = response.Messages.LastOrDefault()
                    ?? throw new InvalidOperationException("The configured chat client returned no response message.");
                history.Add(message);

                var functionCalls = message.Contents
                    .OfType<FunctionCallContent>()
                    .Where(static call => !call.InformationalOnly)
                    .ToArray();
                if (functionCalls.Length == 0)
                {
                    var output = ToAgentMessage(message);
                    return new AgentTurnResult(
                        [output],
                        new AgentTurnTelemetry(
                            Stopwatch.GetElapsedTime(started),
                            toolCallCount,
                            ToTelemetryTokenCount(promptTokens),
                            ToTelemetryTokenCount(completionTokens),
                            modelId));
                }

                if (round == options.MaxToolRoundTrips)
                    throw new InvalidOperationException(
                        $"The chat client exceeded the configured tool round-trip limit of {options.MaxToolRoundTrips}.");

                foreach (var call in functionCalls)
                {
                    if (call.Exception is not null)
                        throw new InvalidOperationException($"The model returned an invalid call for tool '{call.Name}'.", call.Exception);

                    var tool = tools.FirstOrDefault(item =>
                        string.Equals(item.Name, call.Name, StringComparison.OrdinalIgnoreCase));
                    if (tool is null)
                        throw new InvalidOperationException($"The model requested unknown tool '{call.Name}'.");
                    if (string.IsNullOrWhiteSpace(call.CallId))
                        throw new InvalidOperationException($"The model returned tool '{call.Name}' without a call ID.");

                    var arguments = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>());
                    var result = await InvokeToolAsync(context, tool, call, arguments, cancellationToken)
                        .ConfigureAwait(false);
                    history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result)]));
                    toolCallCount++;
                }
            }

            throw new InvalidOperationException("The chat client runtime did not complete the turn.");
        }

        public async IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
            AgentTurnContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Buffering keeps the same approval-checked tool loop for streaming callers.
            yield return await InvokeAsync(context, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
        }

        private List<ChatMessage> BuildMessages(IReadOnlyList<AgentMessage> messages)
        {
            var history = new List<ChatMessage>(messages.Count + 1);
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                history.Add(new ChatMessage(ChatRole.System, systemPrompt));

            history.AddRange(messages.Select(ToChatMessage));
            return history;
        }

        private ChatOptions BuildChatOptions()
            => new()
            {
                ModelId = modelId,
                Temperature = options.Temperature,
                MaxOutputTokens = options.MaxOutputTokens,
                Tools = _modelTools.Count == 0 ? null : _modelTools.ToList(),
            };

        private async ValueTask<string?> InvokeToolAsync(
            AgentTurnContext context,
            ITool tool,
            FunctionCallContent call,
            string arguments,
            CancellationToken cancellationToken)
        {
            var requestedAt = DateTimeOffset.UtcNow;
            var approvalId = ToolApprovalEvaluator.CreateApprovalId(
                context.SessionId,
                tool.Name,
                arguments,
                call.CallId);
            var approvalRequest = new ToolApprovalRequest(
                approvalId,
                context.SessionId,
                tool.Name,
                arguments,
                requestedAt,
                call.CallId);
            var decision = await ToolApprovalEvaluator.EvaluateAsync(
                approvalRequest,
                context.Services,
                cancellationToken).ConfigureAwait(false);

            if (decision.Status == ToolApprovalStatus.Pending)
                throw new ToolApprovalRequiredException(new PendingToolApproval(approvalId, approvalRequest, decision));
            if (decision.Status == ToolApprovalStatus.Rejected)
                throw new InvalidOperationException($"Tool '{tool.Name}' was rejected by the approval policy. {decision.Reason}".Trim());

            var observation = new ToolCallObservation(tool.Name, arguments, call.CallId, context.SessionId, requestedAt);
            foreach (var observer in observers)
                await observer.OnToolCallingAsync(observation, cancellationToken).ConfigureAwait(false);

            var stopwatch = Stopwatch.StartNew();
            string? result = null;
            Exception? failure = null;
            try
            {
                result = await tool.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch (Exception exception)
            {
                failure = exception;
                throw;
            }
            finally
            {
                stopwatch.Stop();
                var completed = new ToolResultObservation(observation, result, failure, stopwatch.Elapsed);
                foreach (var observer in observers)
                    await observer.OnToolCalledAsync(completed, cancellationToken).ConfigureAwait(false);
            }
        }

        private static ChatMessage ToChatMessage(AgentMessage message)
        {
            var role = MapRole(message.Role);
            if (message.ContentParts is not { Count: > 0 } parts)
                return new ChatMessage(role, message.Content);

            var contents = parts.Select(static part => part switch
            {
                TextAgentContent text => (AIContent)new TextContent(text.Text),
                ImageAgentContent image => new DataContent(image.Data.ToArray(), image.MediaType),
                DataAgentContent data => new DataContent(data.Data.ToArray(), data.MediaType),
                AudioAgentContent audio => new DataContent(audio.Data.ToArray(), audio.MediaType),
                VideoAgentContent video => new DataContent(video.Data.ToArray(), video.MediaType),
                DocumentAgentContent document => new DataContent(document.Data.ToArray(), document.MediaType),
                UriImageAgentContent uriImage => new TextContent(uriImage.Uri.ToString()),
                _ => new TextContent(part.ToString() ?? string.Empty),
            });
            return new ChatMessage(role, contents.ToList());
        }

        private static AgentMessage ToAgentMessage(ChatMessage message)
            => AgentMessage.FromText(
                message.Role.ToString().ToLowerInvariant(),
                string.Join("\n", message.Contents.OfType<TextContent>().Select(static content => content.Text)));

        private static ChatRole MapRole(string role) => role.ToLowerInvariant() switch
        {
            "system" => ChatRole.System,
            "assistant" => ChatRole.Assistant,
            "tool" => ChatRole.Tool,
            _ => ChatRole.User,
        };

        private static long? AddTokenCounts(long? current, long? next)
            => current is null && next is null ? null : (current ?? 0) + (next ?? 0);

        private static int? ToTelemetryTokenCount(long? value)
            => value is null ? null : (int)Math.Clamp(value.Value, 0, int.MaxValue);
    }

    private sealed class ToolDeclaration(ITool tool) : AIFunction
    {
        private static readonly JsonElement s_defaultSchema = JsonSerializer.SerializeToElement(new { type = "object" });
        private readonly JsonElement _schema = ParseSchema(tool.ParametersSchemaJson);

        public override string Name => tool.Name;
        public override string Description => tool.Description ?? string.Empty;
        public override JsonElement JsonSchema => _schema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("Tool calls must be executed by the approval-checked MyAgents runtime loop.");

        private static JsonElement ParseSchema(string? schemaJson)
        {
            if (!string.IsNullOrWhiteSpace(schemaJson))
            {
                try
                {
                    return JsonSerializer.Deserialize<JsonElement>(schemaJson);
                }
                catch (JsonException)
                {
                    // Invalid optional metadata falls back to a permissive object schema.
                }
            }

            return s_defaultSchema;
        }
    }
}
