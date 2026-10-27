namespace MyAgents.Core.Workflows;

/// <summary>
/// Represents a pending or completed human-input request within a paused workflow.
/// </summary>
/// <param name="StepId">Identifier for the step that requested input.</param>
/// <param name="Prompt">The question or instruction to show the human.</param>
/// <param name="Reply">The human's reply, populated by <see cref="WorkflowHost.ResumeWithInputAsync"/> before resuming.</param>
public sealed record HumanInputRequest(
    string StepId,
    string Prompt,
    string? Reply = null);

/// <summary>
/// A versioned, serializable snapshot of workflow state for checkpoint/resume.
/// </summary>
/// <param name="WorkflowId">Identifier of the workflow type.</param>
/// <param name="StateVersion">Version of the state schema.</param>
/// <param name="SessionId">Unique session identifier.</param>
/// <param name="StateJson">Serialized state as JSON.</param>
/// <param name="PendingHumanInput">
/// When non-null the workflow is paused and waiting for a human reply.
/// Null for normal (non-HITL) snapshots.
/// </param>
public sealed record WorkflowSnapshot(
    string WorkflowId,
    int StateVersion,
    string SessionId,
    string StateJson,
    HumanInputRequest? PendingHumanInput = null)
{
    /// <summary>
    /// When non-null the workflow is paused and waiting for a tool approval decision.
    /// </summary>
    public PendingToolApproval? PendingToolApproval { get; init; }
}
