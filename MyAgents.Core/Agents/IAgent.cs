namespace MyAgents.Core;

/// <summary>
/// The core agent contract. All agents implement this interface.
/// </summary>
public interface IAgent : IDisposable
{
    /// <summary>
    /// Invokes the agent and waits for the complete response.
    /// </summary>
    /// <param name="context">The turn context containing session ID, messages, and services.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The complete result of the turn execution.</returns>
    ValueTask<AgentTurnResult> InvokeAsync(
        AgentTurnContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes the agent and streams partial results as they arrive.
    /// Implementations that don't support streaming may collect and yield a single result.
    /// </summary>
    /// <param name="context">The turn context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of partial results.</returns>
    IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
        AgentTurnContext context,
        CancellationToken cancellationToken = default);
}
