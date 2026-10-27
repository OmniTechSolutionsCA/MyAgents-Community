namespace MyAgents.Core;

/// <summary>
/// Structured audit record capturing a tool approval evaluation and its outcome.
/// </summary>
public sealed record ToolApprovalAuditRecord(
    string AuditId,
    string ApprovalId,
    string SessionId,
    string ToolName,
    string ArgumentsJson,
    ToolApprovalDecision Decision,
    DateTimeOffset Timestamp,
    TimeSpan Duration,
    string? GateType = null);

/// <summary>
/// Sink for recording and auditing tool approval decisions across the platform.
/// </summary>
public interface IToolApprovalAuditSink
{
    /// <summary>
    /// Records a tool approval audit entry.
    /// </summary>
    ValueTask RecordAsync(ToolApprovalAuditRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns audit records for a given session.
    /// </summary>
    ValueTask<IReadOnlyList<ToolApprovalAuditRecord>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all recorded approval audit records.
    /// </summary>
    ValueTask<IReadOnlyList<ToolApprovalAuditRecord>> ListAllAsync(CancellationToken cancellationToken = default);
}