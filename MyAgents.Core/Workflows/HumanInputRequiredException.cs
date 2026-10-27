namespace MyAgents.Core.Workflows;

/// <summary>
/// Thrown by <see cref="WorkflowSteps.RequestHumanInputAsync{TState}"/> to unwind the workflow
/// execution stack and hand a serializable snapshot back to the caller.
///
/// The caller pattern is:
/// <code>
/// try
/// {
///     var state = await host.RunAsync(workflow, initialState);
/// }
/// catch (HumanInputRequiredException ex)
/// {
///     // Persist ex.Snapshot — it contains the paused state and the prompt to show the user.
///     // Later, collect the human's reply and resume:
///     var finalState = await host.ResumeWithInputAsync(workflow, ex.Snapshot, humanReply);
/// }
/// </code>
/// </summary>
public sealed class HumanInputRequiredException : Exception
{
    /// <summary>
    /// The workflow snapshot frozen at the point human input was requested.
    /// <see cref="WorkflowSnapshot.PendingHumanInput"/> is guaranteed to be non-null.
    /// </summary>
    public WorkflowSnapshot Snapshot { get; }

    /// <summary>
    /// Initialises a new instance with the paused snapshot.
    /// </summary>
    public HumanInputRequiredException(WorkflowSnapshot snapshot)
        : base(CreateMessage(snapshot))
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    private static string CreateMessage(WorkflowSnapshot snapshot)
    {
        if (snapshot.PendingToolApproval is not null)
        {
            return $"Workflow '{snapshot.WorkflowId}' paused for tool approval " +
                   $"'{snapshot.PendingToolApproval.ApprovalId}'. " +
                   $"Provide a decision via WorkflowHost.ResumeWithToolApprovalAsync to resume.";
        }

        return $"Workflow '{snapshot.WorkflowId}' paused at step " +
               $"'{snapshot.PendingHumanInput?.StepId}'. " +
               $"Provide a human reply via WorkflowHost.ResumeWithInputAsync to resume.";
    }
}
