namespace MyAgents.Core;

/// <summary>
/// Internal no-op session used for graph node delegates that do not require
/// an underlying agent runtime session (for example subgraph wrapper nodes).
/// </summary>
internal sealed class NullAgentSession : IAgentSession
{
    public static NullAgentSession Instance { get; } = new();

    private NullAgentSession()
    {
    }

    public string SessionId => "null-session";

    public IReadOnlyList<IAgentCapability> Capabilities => Array.Empty<IAgentCapability>();

    public ValueTask<AgentTurnResult> InvokeAsync(
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("NullAgentSession does not support InvokeAsync.");
    }

    public IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("NullAgentSession does not support InvokeStreamingAsync.");
    }

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
