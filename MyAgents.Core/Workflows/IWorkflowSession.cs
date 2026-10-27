namespace MyAgents.Core.Workflows;

/// <summary>
/// Session context for workflow execution with typed state.
/// </summary>
/// <typeparam name="TState">The workflow state type.</typeparam>
public interface IWorkflowSession<TState>
{
    /// <summary>
    /// Unique identifier for this workflow session.
    /// </summary>
    string SessionId { get; }

    /// <summary>
    /// The current workflow state. Can be mutated during execution.
    /// </summary>
    TState State { get; set; }

    /// <summary>
    /// Calls an agent within this workflow session.
    /// </summary>
    /// <param name="agentName">Name of the agent to call.</param>
    /// <param name="messages">Messages to send to the agent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the agent invocation.</returns>
    ValueTask<AgentTurnResult> CallAgentAsync(
        string agentName,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls an agent within this workflow session and streams partial results.
    /// </summary>
    /// <param name="agentName">Name of the agent to call.</param>
    /// <param name="messages">Messages to send to the agent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of partial results from the agent.</returns>
    IAsyncEnumerable<AgentTurnResult> CallAgentStreamingAsync(
        string agentName,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default);
    /// <summary>
    /// Returns the pending human-input request (with <see cref="HumanInputRequest.Reply"/> populated)
    /// if this session was resumed via <see cref="WorkflowHost.ResumeWithInputAsync"/>.
    /// Returns <c>null</c> for normal (non-HITL-resumed) sessions.
    /// </summary>
    HumanInputRequest? GetPendingHumanInput();

    /// <summary>
    /// Creates a snapshot of the current workflow state for persistence.
    /// </summary>
    /// <returns>A versioned, serializable snapshot.</returns>
    WorkflowSnapshot CreateSnapshot();
}
