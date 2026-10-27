namespace MyAgents.Core.Workflows;

/// <summary>
/// Optional workflow-session interface that exposes a pending tool-approval request after resume.
/// </summary>
public interface IWorkflowApprovalContext
{
    /// <summary>
    /// Returns the pending tool approval attached to the resumed workflow snapshot, if any.
    /// </summary>
    PendingToolApproval? GetPendingToolApproval();
}
