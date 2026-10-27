using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using MyAgents.Core.Tools;

namespace MyAgents.Core;

/// <summary>
/// Runtime-agnostic tool execution middleware.
///
/// When the inner agent returns an assistant message containing a supported
/// function-call envelope, the middleware resolves the requested tool from the
/// session <see cref="ToolCatalog"/>, invokes it, appends a tool message to the
/// conversation, and reinvokes the inner pipeline until the agent returns a
/// normal assistant response.
/// </summary>
/// <remarks>
/// Supported envelopes:
/// <code>
/// { "type": "function_call", "name": "weather", "arguments": { "city": "London" } }
/// { "tool": "weather", "arguments": "{\"city\":\"London\"}" }
/// { "function": { "name": "weather", "arguments": { "city": "London" } }, "id": "call-1" }
/// </code>
/// </remarks>
public sealed class FunctionCallingMiddleware : IAgentMiddleware
{
    private static readonly Counter<long> s_toolCalls =
        MyAgentsTelemetry.Meter.CreateCounter<long>(
            "agent.tool.calls",
            "{calls}",
            "Number of framework-level tool invocations.");

    private static readonly Counter<long> s_toolErrors =
        MyAgentsTelemetry.Meter.CreateCounter<long>(
            "agent.tool.errors",
            "{errors}",
            "Number of framework-level tool invocation failures.");

    private static readonly Histogram<double> s_toolLatency =
        MyAgentsTelemetry.Meter.CreateHistogram<double>(
            "agent.tool.duration_ms",
            "ms",
            "Duration of framework-level tool invocations.");

    private readonly int _maxToolRoundTrips;

    public FunctionCallingMiddleware(int maxToolRoundTrips = 8)
    {
        if (maxToolRoundTrips < 1)
            throw new ArgumentOutOfRangeException(nameof(maxToolRoundTrips), "At least one tool round-trip must be allowed.");

        _maxToolRoundTrips = maxToolRoundTrips;
    }

    public async ValueTask<AgentTurnResult> InvokeAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>> next,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var toolCatalog = context.Services.GetService<ToolCatalog>();
        if (toolCatalog is null)
            return await next(context, cancellationToken).ConfigureAwait(false);

        var observers = context.Services.GetService<IReadOnlyList<IToolCallObserver>>()
            ?? Array.Empty<IToolCallObserver>();
        var approvalPolicy = context.Services.GetService<IToolApprovalPolicy>();
        var approvalStore = context.Services.GetService<IToolApprovalStore>();
        var traceSink = context.Services.GetService<IRunTraceSink>();

        var currentContext = context;
        var accumulatedToolCalls = 0;

        for (var roundTrip = 0; roundTrip <= _maxToolRoundTrips; roundTrip++)
        {
            var result = await next(currentContext, cancellationToken).ConfigureAwait(false);
            var assistantMessage = result.Output.LastOrDefault(static m =>
                string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase));

            if (assistantMessage is null ||
                !AgentFunctionCallSerializer.TryParse(assistantMessage.Content, out var functionCall))
            {
                return accumulatedToolCalls == 0
                    ? result
                    : result with
                    {
                        Telemetry = result.Telemetry with
                        {
                            ToolCalls = result.Telemetry.ToolCalls + accumulatedToolCalls,
                        },
                    };
            }

            if (roundTrip == _maxToolRoundTrips)
            {
                throw new InvalidOperationException(
                    $"Function-calling exceeded the configured limit of {_maxToolRoundTrips} tool round-trip(s).");
            }

            if (!toolCatalog.TryGet(functionCall.Name, out var tool))
            {
                throw new InvalidOperationException(
                    $"The agent requested tool '{functionCall.Name}', but no matching tool was registered.");
            }

            var observation = new ToolCallObservation(
                functionCall.Name,
                functionCall.ArgumentsJson,
                functionCall.CallId,
                currentContext.SessionId,
                DateTimeOffset.UtcNow);

            var approvalId = ToolApprovalEvaluator.CreateApprovalId(
                currentContext.SessionId,
                functionCall.Name,
                functionCall.ArgumentsJson,
                functionCall.CallId);

            var approvalRequest = new ToolApprovalRequest(
                ApprovalId: approvalId,
                SessionId: currentContext.SessionId,
                ToolName: functionCall.Name,
                ArgumentsJson: functionCall.ArgumentsJson,
                RequestedAt: DateTimeOffset.UtcNow,
                CallId: functionCall.CallId);

            if (traceSink is not null)
            {
                await traceSink.RecordAsync(new RunTraceRecord(
                    RunId: currentContext.SessionId,
                    SessionId: currentContext.SessionId,
                    EventKind: RunEventKind.ToolRequested,
                    RecordedAt: approvalRequest.RequestedAt,
                    ToolName: functionCall.Name,
                    RelatedId: functionCall.CallId,
                    PayloadJson: functionCall.ArgumentsJson), cancellationToken).ConfigureAwait(false);
            }

            var policyDecision = await ToolApprovalEvaluator.EvaluateAsync(
                approvalRequest,
                currentContext.Services,
                cancellationToken).ConfigureAwait(false);

            if (policyDecision.Status == ToolApprovalStatus.Pending)
            {
                var pendingApproval = new PendingToolApproval(approvalId, approvalRequest, policyDecision);
                if (traceSink is not null)
                {
                    await traceSink.RecordAsync(new RunTraceRecord(
                        RunId: currentContext.SessionId,
                        SessionId: currentContext.SessionId,
                        EventKind: RunEventKind.ApprovalRequested,
                        RecordedAt: DateTimeOffset.UtcNow,
                        ToolName: functionCall.Name,
                        RelatedId: approvalId,
                        PayloadJson: functionCall.ArgumentsJson), cancellationToken).ConfigureAwait(false);
                }

                throw new ToolApprovalRequiredException(pendingApproval);
            }

            if (policyDecision.Status == ToolApprovalStatus.Rejected)
            {
                if (traceSink is not null)
                {
                    await traceSink.RecordAsync(new RunTraceRecord(
                        RunId: currentContext.SessionId,
                        SessionId: currentContext.SessionId,
                        EventKind: RunEventKind.ApprovalDecided,
                        RecordedAt: DateTimeOffset.UtcNow,
                        ToolName: functionCall.Name,
                        RelatedId: approvalId,
                        PayloadJson: policyDecision.Reason), cancellationToken).ConfigureAwait(false);
                }

                throw new InvalidOperationException(
                    $"Tool '{functionCall.Name}' was rejected by the approval policy. {policyDecision.Reason}".Trim());
            }

            foreach (var observer in observers)
                await observer.OnToolCallingAsync(observation, cancellationToken).ConfigureAwait(false);

            var stopwatch = Stopwatch.StartNew();
            string? toolResult = null;
            Exception? toolException = null;

            using var activity = MyAgentsTelemetry.ActivitySource.StartActivity("agent.tool");
            activity?.SetTag("agent.session_id", currentContext.SessionId);
            activity?.SetTag("agent.tool.name", functionCall.Name);
            if (!string.IsNullOrWhiteSpace(functionCall.CallId))
                activity?.SetTag("agent.tool.call_id", functionCall.CallId);

            try
            {
                toolResult = await tool.InvokeAsync(functionCall.ArgumentsJson, cancellationToken).ConfigureAwait(false);
                activity?.SetTag("agent.tool.success", true);
            }
            catch (Exception ex)
            {
                toolException = ex;
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("agent.tool.success", false);
                s_toolErrors.Add(1,
                    new KeyValuePair<string, object?>("agent.session_id", currentContext.SessionId),
                    new KeyValuePair<string, object?>("agent.tool.name", functionCall.Name));
                throw;
            }
            finally
            {
                stopwatch.Stop();
                activity?.SetTag("agent.tool.duration_ms", stopwatch.Elapsed.TotalMilliseconds);
                s_toolCalls.Add(1,
                    new KeyValuePair<string, object?>("agent.session_id", currentContext.SessionId),
                    new KeyValuePair<string, object?>("agent.tool.name", functionCall.Name));
                s_toolLatency.Record(stopwatch.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("agent.session_id", currentContext.SessionId),
                    new KeyValuePair<string, object?>("agent.tool.name", functionCall.Name));
                var resultObservation = new ToolResultObservation(observation, toolResult, toolException, stopwatch.Elapsed);

                if (traceSink is not null)
                {
                    await traceSink.RecordAsync(new RunTraceRecord(
                        RunId: currentContext.SessionId,
                        SessionId: currentContext.SessionId,
                        EventKind: RunEventKind.ToolCompleted,
                        RecordedAt: DateTimeOffset.UtcNow,
                        ToolName: functionCall.Name,
                        RelatedId: functionCall.CallId,
                        PayloadJson: toolException is null ? toolResult : toolException.Message), cancellationToken).ConfigureAwait(false);
                }

                foreach (var observer in observers)
                    await observer.OnToolCalledAsync(resultObservation, cancellationToken).ConfigureAwait(false);
            }

            accumulatedToolCalls++;

            currentContext = currentContext with
            {
                Messages = currentContext.Messages
                    .Concat(result.Output)
                    .Append(new AgentMessage("tool", toolResult ?? string.Empty, SenderId: functionCall.Name))
                    .ToArray(),
            };
        }

        throw new UnreachableException("Function-calling loop terminated unexpectedly.");
    }

}
