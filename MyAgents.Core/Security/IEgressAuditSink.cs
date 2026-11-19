namespace MyAgents.Core.Security;

/// <summary>
/// Sink for recording and auditing egress defense findings across the platform.
/// </summary>
public interface IEgressAuditSink
{
    /// <summary>
    /// Records an egress audit entry.
    /// </summary>
    ValueTask RecordAsync(EgressAuditRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns audit records for a given session.
    /// </summary>
    ValueTask<IReadOnlyList<EgressAuditRecord>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all recorded egress audit records.
    /// </summary>
    ValueTask<IReadOnlyList<EgressAuditRecord>> ListAllAsync(CancellationToken cancellationToken = default);
}
