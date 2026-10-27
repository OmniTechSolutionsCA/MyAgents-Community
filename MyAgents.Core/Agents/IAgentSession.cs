namespace MyAgents.Core;

/// <summary>
/// Represents an active agent session with conversation state.
/// </summary>
/// <remarks>
/// Implement <see cref="IAsyncDisposable"/> to release resources asynchronously
/// (e.g. open MCP server connections, streaming HTTP connections). The synchronous
/// <see cref="IDisposable.Dispose"/> is retained for backward compatibility and
/// for use in <see langword="using"/> blocks that cannot <see langword="await"/>.
/// Prefer <c>await using</c> over <c>using</c> for sessions created via
/// <see cref="IAgentSessionFactory.CreateSessionAsync"/> so that MCP and other async
/// resources are torn down cleanly.
/// </remarks>
public interface IAgentSession : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Unique identifier for this session.
    /// </summary>
    string SessionId { get; }

    /// <summary>
    /// Returns the full set of capabilities for this session, merging both
    /// definition-level and compose-time (feature-contributed) capabilities.
    /// Use this instead of <see cref="AgentDefinition.HasCapability"/> when you
    /// need to query capabilities after features have run.
    /// </summary>
    IReadOnlyList<IAgentCapability> Capabilities { get; }

    /// <summary>
    /// Invokes a turn within this session and waits for the full response.
    /// </summary>
    /// <param name="messages">Messages for this turn.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The complete result of the turn execution.</returns>
    ValueTask<AgentTurnResult> InvokeAsync(
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes a turn and streams partial results as they arrive.
    /// Each yielded result contains one or more new output chunks.
    /// </summary>
    /// <param name="messages">Messages for this turn.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of partial results.</returns>
    IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default);
}
