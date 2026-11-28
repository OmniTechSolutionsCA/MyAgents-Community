namespace MyAgents.Core;

/// <summary>
/// Approval decision for a tool invocation.
/// </summary>
public sealed record ToolApprovalDecision(
    ToolApprovalStatus Status,
    DateTimeOffset DecidedAt,
    string? DecidedBy = null,
    string? Reason = null,
    ApprovalScope Scope = ApprovalScope.Once,
    string? ModifiedArgumentsJson = null)
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
    /// Creates an approval decision that overrides the tool's arguments with edited JSON.
    /// </summary>
    public static ToolApprovalDecision ApproveWithEdits(
        string modifiedArgumentsJson,
        string? decidedBy = null,
        string? reason = null,
        ApprovalScope scope = ApprovalScope.Once)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modifiedArgumentsJson);
        using var _ = System.Text.Json.JsonDocument.Parse(modifiedArgumentsJson);
        return new(ToolApprovalStatus.Approved, DateTimeOffset.UtcNow, decidedBy, reason, scope, modifiedArgumentsJson);
    }

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