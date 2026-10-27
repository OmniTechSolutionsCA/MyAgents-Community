namespace MyAgents.Core.Tools;

// ── Tool call observation types ───────────────────────────────────────────────

/// <summary>
/// Describes a tool call that was about to be (or was) invoked by the agent.
/// </summary>
/// <param name="ToolName">The name of the tool being called.</param>
/// <param name="Arguments">The raw JSON-serialised argument string, if available.</param>
/// <param name="CallId">The function-call id provided by the model, if available.</param>
/// <param name="SessionId">The session in which the call occurred.</param>
/// <param name="OccurredAt">UTC timestamp of the observation.</param>
public sealed record ToolCallObservation(
    string ToolName,
    string? Arguments,
    string? CallId,
    string SessionId,
    DateTimeOffset OccurredAt);

/// <summary>
/// Describes the result of a completed tool call.
/// </summary>
/// <param name="Call">The original call observation.</param>
/// <param name="Result">The tool's return value as a string, or null if not available.</param>
/// <param name="Exception">The exception thrown by the tool, if any.</param>
/// <param name="Duration">Wall-clock time taken by the tool invocation.</param>
public sealed record ToolResultObservation(
    ToolCallObservation Call,
    string? Result,
    Exception? Exception,
    TimeSpan Duration);

// ── Observer interface ────────────────────────────────────────────────────────

/// <summary>
/// Intercepts tool call/result pairs at the framework layer.
/// Observers are called for every function call the model emits, regardless
/// of which tool it targets.
///
/// <para>
/// Add observers to <see cref="AgentAssembly.ToolObservers"/> from an
/// <see cref="IAgentFeature.Contribute(AgentAssembly)"/> implementation,
/// or directly on the assembly before session creation.
/// </para>
/// </summary>
/// <remarks>
/// Observers are called synchronously on the turn thread.
/// Keep implementations fast; offload heavy work (DB writes, HTTP calls) to a background queue.
/// </remarks>
public interface IToolCallObserver
{
    /// <summary>
    /// Called immediately before the tool is invoked.
    /// Throw to abort the call (the exception propagates to the agent pipeline).
    /// </summary>
    ValueTask OnToolCallingAsync(
        ToolCallObservation call,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    /// <summary>
    /// Called after the tool returns (or throws).
    /// <paramref name="result"/>.<see cref="ToolResultObservation.Exception"/> is non-null when
    /// the tool threw.
    /// </summary>
    ValueTask OnToolCalledAsync(
        ToolResultObservation result,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
