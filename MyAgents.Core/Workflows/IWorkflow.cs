namespace MyAgents.Core.Workflows;

/// <summary>
/// A workflow that orchestrates multiple agents with typed state.
/// </summary>
/// <typeparam name="TState">The type of state maintained during workflow execution.</typeparam>
public interface IWorkflow<TState>
{
    /// <summary>
    /// Unique identifier for this workflow type.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Version of the state schema (for migration compatibility).
    /// </summary>
    int StateVersion { get; }

    /// <summary>
    /// Runs the workflow against the given session.
    /// </summary>
    /// <param name="session">The workflow session for state and agent calls.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task representing workflow completion.</returns>
    ValueTask RunAsync(IWorkflowSession<TState> session, CancellationToken cancellationToken = default);
}
