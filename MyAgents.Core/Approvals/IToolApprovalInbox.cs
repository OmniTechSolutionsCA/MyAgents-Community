namespace MyAgents.Core;

/// <summary>
/// Optional query surface for enumerating approval records across sessions.
/// </summary>
public interface IToolApprovalInbox
{
    /// <summary>
    /// Lists all approvals ordered by request time descending when supported.
    /// </summary>
    ValueTask<IReadOnlyList<PendingToolApproval>> ListAllAsync(CancellationToken cancellationToken = default);
}
