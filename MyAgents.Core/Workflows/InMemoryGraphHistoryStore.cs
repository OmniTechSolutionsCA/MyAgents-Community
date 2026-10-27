using System.Collections.Concurrent;

namespace MyAgents.Core.Workflows;

/// <summary>
/// In-process, in-memory implementation of <see cref="IGraphHistoryStore"/>.
/// Snapshots are stored for the lifetime of the object and lost when it is garbage-collected.
/// Suitable for testing and single-process deployments; for distributed or durable scenarios
/// replace with a database-backed implementation.
/// </summary>
/// <remarks>
/// Thread-safe: multiple concurrent calls to <see cref="SaveSnapshotAsync"/> and
/// <see cref="ListSnapshotsAsync"/> are supported without external synchronisation.
/// </remarks>
public sealed class InMemoryGraphHistoryStore : IGraphHistoryStore, IGraphHistoryCatalog
{
    private readonly ConcurrentDictionary<string, GraphSnapshot> _byId =
        new(StringComparer.Ordinal);

    // Per-session ordered lists are guarded by _sessionLock to keep insertion order deterministic.
    private readonly ConcurrentDictionary<string, List<GraphSnapshot>> _bySession =
        new(StringComparer.Ordinal);

    private readonly object _sessionLock = new();
    private readonly int _maxSnapshotsPerSession;

    /// <summary>
    /// Creates a new in-memory graph history store.
    /// </summary>
    /// <param name="maxSnapshotsPerSession">
    /// Maximum number of snapshots retained per session. Older snapshots are evicted first.
    /// Use <see cref="int.MaxValue"/> to disable eviction.
    /// </param>
    public InMemoryGraphHistoryStore(int maxSnapshotsPerSession = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSnapshotsPerSession, 1);
        _maxSnapshotsPerSession = maxSnapshotsPerSession;
    }

    /// <inheritdoc/>
    public ValueTask SaveSnapshotAsync(GraphSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _byId[snapshot.SnapshotId] = snapshot;

        lock (_sessionLock)
        {
            if (!_bySession.TryGetValue(snapshot.SessionId, out var list))
            {
                list = new List<GraphSnapshot>();
                _bySession[snapshot.SessionId] = list;
            }

            list.Add(snapshot);

            while (list.Count > _maxSnapshotsPerSession)
            {
                var evicted = list[0];
                list.RemoveAt(0);
                _byId.TryRemove(evicted.SnapshotId, out _);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<GraphSnapshot>> ListSnapshotsAsync(
        string sessionId, CancellationToken ct = default)
    {
        IReadOnlyList<GraphSnapshot> result;

        lock (_sessionLock)
        {
            result = _bySession.TryGetValue(sessionId, out var list)
                ? list.OrderBy(s => s.StateVersion).ToList()
                : Array.Empty<GraphSnapshot>();
        }

        return ValueTask.FromResult(result);
    }

    /// <inheritdoc/>
    public ValueTask<GraphSnapshot?> GetSnapshotAsync(
        string snapshotId, CancellationToken ct = default)
    {
        _byId.TryGetValue(snapshotId, out var snapshot);
        return ValueTask.FromResult(snapshot);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> ListSessionIdsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<string> result;
        lock (_sessionLock)
        {
            result = _bySession
                .Select(static pair => new
                {
                    SessionId = pair.Key,
                    LastUpdatedAt = pair.Value.Count == 0
                        ? DateTimeOffset.MinValue
                        : pair.Value.Max(static snapshot => snapshot.CreatedAt),
                })
                .OrderByDescending(static item => item.LastUpdatedAt)
                .Select(static item => item.SessionId)
                .ToArray();
        }

        return ValueTask.FromResult(result);
    }

    /// <summary>
    /// Removes all snapshots for the specified session.
    /// </summary>
    public void ClearSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_sessionLock)
        {
            if (!_bySession.TryRemove(sessionId, out var list))
                return;

            foreach (var snapshot in list)
                _byId.TryRemove(snapshot.SnapshotId, out _);
        }
    }

    /// <summary>
    /// Removes all retained snapshots across all sessions.
    /// </summary>
    public void Clear()
    {
        lock (_sessionLock)
        {
            _bySession.Clear();
            _byId.Clear();
        }
    }
}
