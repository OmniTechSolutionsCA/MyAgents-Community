namespace MyAgents.Core;

/// <summary>
/// Stores pending and resolved tool approvals for workflows, sessions, and local studio inspection.
/// </summary>
public interface IToolApprovalStore
{
    /// <summary>
    /// Saves or replaces an approval record.
    /// </summary>
    ValueTask SaveAsync(PendingToolApproval approval, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a previously stored approval by ID.
    /// </summary>
    ValueTask<PendingToolApproval?> GetAsync(string approvalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists approvals for a session ordered by request time.
    /// </summary>
    ValueTask<IReadOnlyList<PendingToolApproval>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken = default);
}
