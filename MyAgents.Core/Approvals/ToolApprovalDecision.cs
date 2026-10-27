namespace MyAgents.Core;

/// <summary>
/// Approval decision for a tool invocation.
/// </summary>
public sealed record ToolApprovalDecision(
    ToolApprovalStatus Status,
    DateTimeOffset DecidedAt,
    string? DecidedBy = null,
    string? Reason = null,
    ApprovalScope Scope = ApprovalScope.Once)
{
    /// <summary>
    /// Creates an approval decision with optional decision maker, reason, and approval scope.
    /// </summary>
    public static ToolApprovalDecision Approve(
        string? decidedBy = null,
        string? reason = null,
        ApprovalScope scope = ApprovalScope.Once)
        => new(ToolApprovalStatus.Approved, DateTimeOffset.UtcNow, decidedBy, reason, scope);

    /// <summary>
    /// Creates a rejection decision.
    /// </summary>
    public static ToolApprovalDecision Reject(string? decidedBy = null, string? reason = null)
        => new(ToolApprovalStatus.Rejected, DateTimeOffset.UtcNow, decidedBy, reason);

    /// <summary>
    /// Creates a pending approval marker.
    /// </summary>
    public static ToolApprovalDecision Pending(string? reason = null)
        => new(ToolApprovalStatus.Pending, DateTimeOffset.UtcNow, null, reason);
}