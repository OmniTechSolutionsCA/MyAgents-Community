using System.Collections.Concurrent;

namespace MyAgents.Core;

/// <summary>
/// In-memory implementation of <see cref="IRunTraceSink"/> for tests, samples, and local-only scenarios.
/// </summary>
public sealed class InMemoryRunTraceSink : IRunTraceSink, IRunTraceCatalog
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<RunTraceRecord>> _records = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public ValueTask RecordAsync(RunTraceRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var queue = _records.GetOrAdd(record.RunId, static _ => new ConcurrentQueue<RunTraceRecord>());
        queue.Enqueue(record);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<RunTraceRecord>> ListAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();

        if (_records.TryGetValue(runId, out var queue))
            return ValueTask.FromResult<IReadOnlyList<RunTraceRecord>>(queue.OrderBy(static item => item.RecordedAt).ToArray());

        return ValueTask.FromResult<IReadOnlyList<RunTraceRecord>>(Array.Empty<RunTraceRecord>());
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListRunIdsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var runIds = _records
            .OrderByDescending(static pair => pair.Value.TryPeek(out var record) ? record.RecordedAt : DateTimeOffset.MinValue)
            .Select(static pair => pair.Key)
            .ToArray();

        return ValueTask.FromResult<IReadOnlyList<string>>(runIds);
    }
}
