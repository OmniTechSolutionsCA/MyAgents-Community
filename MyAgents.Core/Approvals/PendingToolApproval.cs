namespace MyAgents.Core;

/// <summary>
/// A persisted approval record for a tool invocation.
/// </summary>
public sealed record PendingToolApproval(
    string ApprovalId,
    ToolApprovalRequest Request,
    ToolApprovalDecision Decision);
