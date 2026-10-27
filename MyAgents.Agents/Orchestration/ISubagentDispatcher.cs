using MyAgents.Core;

namespace MyAgents.Agents;

/// <summary>
/// Dispatcher contract for delegating, forking, and managing hierarchical subagent executions.
/// </summary>
public interface ISubagentDispatcher
{
    /// <summary>
    /// Gets the names of all registered subagents available for delegation.
    /// </summary>
    IReadOnlyList<string> GetAvailableSubagentNames();

    /// <summary>
    /// Retrieves the agent definition for a given subagent name, if registered.
    /// </summary>
    AgentDefinition? GetSubagentDefinition(string agentName);

    /// <summary>
    /// Synchronously delegates a task to a subagent and awaits its final distilled output.
    /// </summary>
    ValueTask<SubagentResult> DelegateAsync(SubagentTask task, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams partial turn results from a subagent turn in real time.
    /// </summary>
    IAsyncEnumerable<AgentTurnResult> DelegateStreamingAsync(SubagentTask task, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously forks a task to run in the background and returns an execution handle immediately.
    /// </summary>
    ValueTask<SubagentTaskHandle> ForkAsync(SubagentTask task, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current status or final result of a background subagent task.
    /// </summary>
    ValueTask<SubagentResult?> GetTaskResultAsync(string taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a running background subagent task.
    /// </summary>
    ValueTask<bool> CancelTaskAsync(string taskId, CancellationToken cancellationToken = default);
}