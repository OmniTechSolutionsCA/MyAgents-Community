using System.Collections.Concurrent;

namespace MyAgents.Core.Security;

/// <summary>
/// Thread-safe in-memory sink for egress audit logging.
/// </summary>
public sealed class InMemoryEgressAuditSink : IEgressAuditSink
{
    private readonly ConcurrentQueue<EgressAuditRecord> _records = new();

    public ValueTask RecordAsync(EgressAuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        _records.Enqueue(record);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<EgressAuditRecord>> ListBySessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        var matches = _records
            .Where(r => string.Equals(r.SessionId, sessionId, StringComparison.Ordinal))
            .OrderBy(r => r.Timestamp)
            .ToArray();

        return ValueTask.FromResult<IReadOnlyList<EgressAuditRecord>>(matches);
    }

    public ValueTask<IReadOnlyList<EgressAuditRecord>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<EgressAuditRecord>>(_records.ToArray());
    }
}
