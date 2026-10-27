namespace MyAgents.Core;

/// <summary>
/// Thrown when a tool invocation requires approval before it may run.
/// </summary>
public sealed class ToolApprovalRequiredException : Exception
{
    /// <summary>
    /// The approval that must be decided before the tool may run.
    /// </summary>
    public PendingToolApproval Approval { get; }

    public ToolApprovalRequiredException(PendingToolApproval approval)
        : base($"Tool '{approval.Request.ToolName}' requires approval. Approval ID: {approval.ApprovalId}.")
    {
        Approval = approval ?? throw new ArgumentNullException(nameof(approval));
    }
}
