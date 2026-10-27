using System.Collections.Concurrent;

namespace MyAgents.Core;

/// <summary>
/// Returns a fixed assistant message on any exception. Never rethrows.
/// </summary>
public sealed class StaticFallbackPolicy : IFallbackPolicy
{
    private readonly string _fallbackMessage;

    /// <param name="fallbackMessage">
    /// The message content to return when an exception occurs.
    /// Defaults to a generic apology.
    /// </param>
    public StaticFallbackPolicy(
        string fallbackMessage = "I'm sorry, something went wrong. Please try again.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackMessage);
        _fallbackMessage = fallbackMessage;
    }

    /// <inheritdoc />
    public ValueTask<AgentTurnResult> HandleAsync(
        Exception ex,
        AgentTurnContext context,
        CancellationToken cancellationToken = default)
    {
        var result = new AgentTurnResult(
            Output: new[] { new AgentMessage("assistant", _fallbackMessage) },
            Telemetry: new AgentTurnTelemetry(TimeSpan.Zero));

        return ValueTask.FromResult(result);
    }
}

/// <summary>
/// Enqueues a <see cref="DeadLetter"/> to the provided queue and returns a fixed fallback message.
/// </summary>
public sealed class DeadLetterFallbackPolicy : IFallbackPolicy
{
    private readonly IDeadLetterQueue _queue;
    private readonly string _fallbackMessage;

    /// <param name="queue">The dead-letter queue to receive failed turns.</param>
    /// <param name="fallbackMessage">
    /// The message content to return to the caller.
    /// Defaults to a generic apology.
    /// </param>
    public DeadLetterFallbackPolicy(
        IDeadLetterQueue queue,
        string fallbackMessage = "I'm sorry, something went wrong. Your request has been logged.")
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackMessage);
        _fallbackMessage = fallbackMessage;
    }

    /// <inheritdoc />
    public async ValueTask<AgentTurnResult> HandleAsync(
        Exception ex,
        AgentTurnContext context,
        CancellationToken cancellationToken = default)
    {
        var entry = new DeadLetter(
            SessionId: context.SessionId,
            Messages: context.Messages,
            Exception: ex,
            FailedAt: DateTimeOffset.UtcNow);

        await _queue.EnqueueAsync(entry, cancellationToken).ConfigureAwait(false);

        var result = new AgentTurnResult(
            Output: new[] { new AgentMessage("assistant", _fallbackMessage) },
            Telemetry: new AgentTurnTelemetry(TimeSpan.Zero));

        return result;
    }
}

/// <summary>
/// In-memory <see cref="IDeadLetterQueue"/> backed by a <see cref="ConcurrentQueue{T}"/>.
/// Intended for testing and local development.
/// </summary>
public sealed class InMemoryDeadLetterQueue : IDeadLetterQueue
{
    private readonly ConcurrentQueue<DeadLetter> _queue = new();

    /// <summary>All dead-letter entries enqueued so far (in arrival order).</summary>
    public IReadOnlyCollection<DeadLetter> Items => _queue;

    /// <inheritdoc />
    public ValueTask EnqueueAsync(DeadLetter entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _queue.Enqueue(entry);
        return ValueTask.CompletedTask;
    }
}
