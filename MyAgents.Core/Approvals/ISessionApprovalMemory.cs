namespace MyAgents.Core;

/// <summary>
/// Maintains active session-scoped tool approval grants (e.g. "Always allow this tool for this session").
/// </summary>
public interface ISessionApprovalMemory
{
    /// <summary>
    /// Records an approved decision with its designated scope into session memory.
    /// </summary>
    ValueTask RecordDecisionAsync(
        ToolApprovalRequest request,
        ToolApprovalDecision decision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a valid active grant exists in session memory for the given request.
    /// </summary>
    ValueTask<ToolApprovalDecision?> TryGetGrantedDecisionAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all stored grants for a specific session.
    /// </summary>
    ValueTask ClearSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}