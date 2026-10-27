using System.Collections.Concurrent;

namespace MyAgents.Core;

/// <summary>
/// Thread-safe in-memory sink for tool approval audit logging.
/// </summary>
public sealed class InMemoryToolApprovalAuditSink : IToolApprovalAuditSink
{
    private readonly ConcurrentQueue<ToolApprovalAuditRecord> _records = new();

    public ValueTask RecordAsync(ToolApprovalAuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        _records.Enqueue(record);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<ToolApprovalAuditRecord>> ListBySessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        var matches = _records
            .Where(r => string.Equals(r.SessionId, sessionId, StringComparison.Ordinal))
            .OrderBy(r => r.Timestamp)
            .ToArray();

        return ValueTask.FromResult<IReadOnlyList<ToolApprovalAuditRecord>>(matches);
    }

    public ValueTask<IReadOnlyList<ToolApprovalAuditRecord>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<ToolApprovalAuditRecord>>(_records.ToArray());
    }
}