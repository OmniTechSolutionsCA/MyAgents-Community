namespace MyAgents.Core;

/// <summary>
/// Abstraction for handling exceptions that escape the agent pipeline.
/// Implementations decide whether to return a fallback result, enqueue a dead-letter,
/// rethrow, or take any other recovery action.
/// </summary>
public interface IFallbackPolicy
{
    /// <summary>
    /// Called when an unhandled exception escapes the agent pipeline.
    /// </summary>
    /// <param name="ex">The exception that was thrown.</param>
    /// <param name="context">The turn context active when the exception occurred.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A fallback <see cref="AgentTurnResult"/> to return to the caller,
    /// or re-throws if recovery is not possible.
    /// </returns>
    ValueTask<AgentTurnResult> HandleAsync(
        Exception ex,
        AgentTurnContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Abstraction for a dead-letter sink that stores failed agent turns for later
/// inspection, replay, or alerting.
/// </summary>
public interface IDeadLetterQueue
{
    /// <summary>Enqueues a dead-letter entry.</summary>
    ValueTask EnqueueAsync(DeadLetter entry, CancellationToken cancellationToken = default);
}

/// <summary>
/// A dead-letter record capturing the context and exception of a failed agent turn.
/// </summary>
/// <param name="SessionId">Session identifier of the failed turn.</param>
/// <param name="Messages">The messages that triggered the failure.</param>
/// <param name="Exception">The exception that caused the failure.</param>
/// <param name="FailedAt">UTC timestamp of the failure.</param>
public sealed record DeadLetter(
    string SessionId,
    IReadOnlyList<AgentMessage> Messages,
    Exception Exception,
    DateTimeOffset FailedAt);
