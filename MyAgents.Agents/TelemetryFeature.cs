using System.Diagnostics;
using System.Diagnostics.Metrics;
using MyAgents.Core;

namespace MyAgents.Agents;

/// <summary>
/// Framework-level OpenTelemetry activity source.
/// Emits spans under the "MyAgents" source name, complementing MEAI's
/// "Microsoft.Extensions.AI" source which covers the LLM call layer.
/// </summary>
/// <remarks>
/// <para>Tags emitted (MyAgents convention):</para>
/// <list type="bullet">
///   <item><description>agent.session_id — from AgentTurnContext.SessionId</description></item>
///   <item><description>agent.turn.tool_calls — from AgentTurnResult.Telemetry.ToolCalls</description></item>
///   <item><description>agent.turn.duration_ms — from AgentTurnResult.Telemetry.TurnDuration</description></item>
/// </list>
///
/// <para>Tags emitted (GenAI semantic convention):</para>
/// <list type="bullet">
///   <item><description>gen_ai.operation.name — "chat.completion"</description></item>
///   <item><description>gen_ai.request.model — from AgentTurnResult.Telemetry.ModelId (if available)</description></item>
///   <item><description>gen_ai.response.model — same as request.model</description></item>
///   <item><description>gen_ai.usage.input_tokens — from AgentTurnResult.Telemetry.PromptTokens</description></item>
///   <item><description>gen_ai.usage.output_tokens — from AgentTurnResult.Telemetry.CompletionTokens</description></item>
///   <item><description>gen_ai.usage.total_tokens — computed PromptTokens + CompletionTokens</description></item>
/// </list>
/// </remarks>
public static class MyAgentsActivitySource
{
    /// <summary>
    /// The activity source used by the MyAgents framework middleware layer.
    /// Register this via <c>tracerProviderBuilder.AddSource("MyAgents")</c> in your OTel setup.
    /// </summary>
    public static readonly ActivitySource Source = MyAgentsTelemetry.ActivitySource;
}

/// <summary>
/// Middleware that wraps each agent turn in an OpenTelemetry Activity span.
/// Tags emitted (MyAgents convention):
///   agent.session_id       — from AgentTurnContext.SessionId
///   agent.turn.tool_calls  — from AgentTurnResult.Telemetry.ToolCalls
///   agent.turn.duration_ms — from AgentTurnResult.Telemetry.TurnDuration
/// Tags emitted (GenAI convention):
///   gen_ai.operation.name        — "chat.completion"
///   gen_ai.request.model         — from AgentTurnResult.Telemetry.ModelId
///   gen_ai.response.model        — from AgentTurnResult.Telemetry.ModelId
///   gen_ai.usage.input_tokens    — from AgentTurnResult.Telemetry.PromptTokens
///   gen_ai.usage.output_tokens   — from AgentTurnResult.Telemetry.CompletionTokens
///   gen_ai.usage.total_tokens    — PromptTokens + CompletionTokens
/// On exception, the span status is set to Error with the exception message.
/// </summary>
public sealed class TelemetryMiddleware : IAgentMiddleware
{
    // ── OTel Metrics ────────────────────────────────────────────────────────
    private static readonly Meter _meter = MyAgentsTelemetry.Meter;

    /// <summary>Histogram of agent turn durations in milliseconds.</summary>
    private static readonly Histogram<double> _turnLatency =
        _meter.CreateHistogram<double>("agent.turn.duration_ms", "ms", "Duration of each agent turn.");

    /// <summary>Counter of prompt (input) tokens consumed per turn.</summary>
    private static readonly Counter<long> _promptTokens =
        _meter.CreateCounter<long>("agent.turn.prompt_tokens", "tokens", "Prompt (input) tokens consumed per turn.");

    /// <summary>Counter of completion (output) tokens produced per turn.</summary>
    private static readonly Counter<long> _completionTokens =
        _meter.CreateCounter<long>("agent.turn.completion_tokens", "tokens", "Completion (output) tokens produced per turn.");

    /// <summary>Counter of total tokens (prompt + completion) consumed per turn.</summary>
    private static readonly Counter<long> _totalTokens =
        _meter.CreateCounter<long>("agent.turn.total_tokens", "tokens", "Total tokens (prompt + completion) consumed per turn.");

    /// <summary>Counter of failed (excepted) turns.</summary>
    private static readonly Counter<long> _turnErrors =
        _meter.CreateCounter<long>("agent.turn.errors", "{errors}", "Number of failed agent turns.");

    public async ValueTask<AgentTurnResult> InvokeAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>> next,
        CancellationToken cancellationToken = default)
    {
        using var activity = MyAgentsActivitySource.Source.StartActivity("agent.turn");
        activity?.SetTag("agent.session_id", context.SessionId);
        var traceSink = context.Services.GetService<IRunTraceSink>();

        if (traceSink is not null)
        {
            await traceSink.RecordAsync(new RunTraceRecord(
                RunId: context.SessionId,
                SessionId: context.SessionId,
                EventKind: RunEventKind.TurnStarted,
                RecordedAt: DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var result = await next(context, cancellationToken).ConfigureAwait(false);

            // MyAgents convention
            activity?.SetTag("agent.turn.tool_calls", result.Telemetry.ToolCalls);
            activity?.SetTag("agent.turn.duration_ms", result.Telemetry.TurnDuration.TotalMilliseconds);
            if (result.Telemetry.PromptTokens.HasValue)
                activity?.SetTag("agent.turn.prompt_tokens", result.Telemetry.PromptTokens.Value);
            if (result.Telemetry.CompletionTokens.HasValue)
                activity?.SetTag("agent.turn.completion_tokens", result.Telemetry.CompletionTokens.Value);

            // GenAI semantic conventions
            activity?.SetTag("gen_ai.operation.name", "chat.completion");
            if (!string.IsNullOrEmpty(result.Telemetry.ModelId))
            {
                activity?.SetTag("gen_ai.request.model", result.Telemetry.ModelId);
                activity?.SetTag("gen_ai.response.model", result.Telemetry.ModelId);
            }
            if (result.Telemetry.PromptTokens.HasValue)
                activity?.SetTag("gen_ai.usage.input_tokens", result.Telemetry.PromptTokens.Value);
            if (result.Telemetry.CompletionTokens.HasValue)
                activity?.SetTag("gen_ai.usage.output_tokens", result.Telemetry.CompletionTokens.Value);
            if (result.Telemetry.TotalTokens.HasValue)
                activity?.SetTag("gen_ai.usage.total_tokens", result.Telemetry.TotalTokens.Value);

            var sessionTag = new KeyValuePair<string, object?>("agent.session_id", context.SessionId);
            _turnLatency.Record(result.Telemetry.TurnDuration.TotalMilliseconds, sessionTag);
            if (result.Telemetry.PromptTokens.HasValue)
                _promptTokens.Add(result.Telemetry.PromptTokens.Value, sessionTag);
            if (result.Telemetry.CompletionTokens.HasValue)
                _completionTokens.Add(result.Telemetry.CompletionTokens.Value, sessionTag);
            if (result.Telemetry.TotalTokens.HasValue)
                _totalTokens.Add(result.Telemetry.TotalTokens.Value, sessionTag);

            if (traceSink is not null)
            {
                await traceSink.RecordAsync(new RunTraceRecord(
                    RunId: context.SessionId,
                    SessionId: context.SessionId,
                    EventKind: RunEventKind.TurnCompleted,
                    RecordedAt: DateTimeOffset.UtcNow,
                    PayloadJson: System.Text.Json.JsonSerializer.Serialize(result.Telemetry)), cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _turnErrors.Add(1,
                new KeyValuePair<string, object?>("agent.session_id", context.SessionId));
            throw;
        }
    }

    /// <summary>
    /// Streaming override: wraps the stream in a single "agent.turn.streaming" Activity span.
    /// Tags are set from the last chunk received (which carries cumulative telemetry).
    /// </summary>
    public async IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, IAsyncEnumerable<AgentTurnResult>> next,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var activity = MyAgentsActivitySource.Source.StartActivity("agent.turn.streaming");
        activity?.SetTag("agent.session_id", context.SessionId);
        var traceSink = context.Services.GetService<IRunTraceSink>();

        if (traceSink is not null)
        {
            await traceSink.RecordAsync(new RunTraceRecord(
                RunId: context.SessionId,
                SessionId: context.SessionId,
                EventKind: RunEventKind.TurnStarted,
                RecordedAt: DateTimeOffset.UtcNow,
                PayloadJson: "streaming"), cancellationToken).ConfigureAwait(false);
        }

        AgentTurnResult? last = null;
        var succeeded = false;

        IAsyncEnumerable<AgentTurnResult> Enumerate()
        {
            try
            {
                return next(context, cancellationToken);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }
        }

        await foreach (var chunk in Enumerate().ConfigureAwait(false))
        {
            last = chunk;
            yield return chunk;
        }

        succeeded = true;

        // Set activity span tags from last chunk
        if (last is not null)
        {
            // MyAgents convention
            activity?.SetTag("agent.turn.tool_calls", last.Telemetry.ToolCalls);
            activity?.SetTag("agent.turn.duration_ms", last.Telemetry.TurnDuration.TotalMilliseconds);
            if (last.Telemetry.PromptTokens.HasValue)
                activity?.SetTag("agent.turn.prompt_tokens", last.Telemetry.PromptTokens.Value);
            if (last.Telemetry.CompletionTokens.HasValue)
                activity?.SetTag("agent.turn.completion_tokens", last.Telemetry.CompletionTokens.Value);

            // GenAI semantic conventions
            activity?.SetTag("gen_ai.operation.name", "chat.completion");
            if (!string.IsNullOrEmpty(last.Telemetry.ModelId))
            {
                activity?.SetTag("gen_ai.request.model", last.Telemetry.ModelId);
                activity?.SetTag("gen_ai.response.model", last.Telemetry.ModelId);
            }
            if (last.Telemetry.PromptTokens.HasValue)
                activity?.SetTag("gen_ai.usage.input_tokens", last.Telemetry.PromptTokens.Value);
            if (last.Telemetry.CompletionTokens.HasValue)
                activity?.SetTag("gen_ai.usage.output_tokens", last.Telemetry.CompletionTokens.Value);
            if (last.Telemetry.TotalTokens.HasValue)
                activity?.SetTag("gen_ai.usage.total_tokens", last.Telemetry.TotalTokens.Value);
        }

        // Record metrics from last chunk
        if (last is not null)
        {
            var sessionTag = new KeyValuePair<string, object?>("agent.session_id", context.SessionId);
            _turnLatency.Record(last.Telemetry.TurnDuration.TotalMilliseconds, sessionTag);
            if (last.Telemetry.PromptTokens.HasValue)
                _promptTokens.Add(last.Telemetry.PromptTokens.Value, sessionTag);
            if (last.Telemetry.CompletionTokens.HasValue)
                _completionTokens.Add(last.Telemetry.CompletionTokens.Value, sessionTag);
            if (last.Telemetry.TotalTokens.HasValue)
                _totalTokens.Add(last.Telemetry.TotalTokens.Value, sessionTag);

            if (traceSink is not null)
            {
                await traceSink.RecordAsync(new RunTraceRecord(
                    RunId: context.SessionId,
                    SessionId: context.SessionId,
                    EventKind: RunEventKind.TurnCompleted,
                    RecordedAt: DateTimeOffset.UtcNow,
                    PayloadJson: System.Text.Json.JsonSerializer.Serialize(last.Telemetry)), cancellationToken).ConfigureAwait(false);
            }
        }

        if (!succeeded)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Streaming enumeration did not complete.");
            _turnErrors.Add(1,
                new KeyValuePair<string, object?>("agent.session_id", context.SessionId));
        }
    }
}

/// <summary>
/// Feature that adds <see cref="TelemetryMiddleware"/> to the agent pipeline.
/// Compose alongside <see cref="LoggingFeature"/> — order determines span nesting.
/// Typically added after <see cref="LoggingFeature"/> so the OTel span is the outermost wrapper.
/// </summary>
/// <example>
/// <code>
/// var agent = new AgentDefinition(
///     Name: "MyAgent",
///     Features: new IAgentFeature[]
///     {
///         new TelemetryFeature(),   // outermost — span wraps everything
///         new LoggingFeature(),
///         new GuardrailsFeature(),
///     });
/// </code>
/// </example>
public sealed class TelemetryFeature : IAgentFeature
{
    public void Contribute(AgentAssembly assembly)
        => assembly.Pipeline.Use(new TelemetryMiddleware());
}
