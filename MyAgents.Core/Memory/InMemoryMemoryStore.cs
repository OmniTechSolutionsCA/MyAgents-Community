using System.Collections.Concurrent;

namespace MyAgents.Core.Memory;

/// <summary>
/// Thread-safe, in-process implementation of <see cref="IMemoryStore"/>.
/// Entries are partitioned by namespace in a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// for O(1) lookups and isolated namespace searches.
/// Suitable for single-process agents and tests. For multi-process or persistent memory,
/// replace with a vector-store or Redis-backed implementation behind the same interface.
/// </summary>
public sealed class InMemoryMemoryStore : IMemoryStore, IMemoryStoreChangeNotifier
{
    // Namespace -> (Key -> MemoryEntry)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, MemoryEntry>> _namespaces = new(StringComparer.Ordinal);
    private readonly Lock _listenersLock = new();
    private Action<MemoryStoreChangedEvent>[] _listeners = [];

    /// <inheritdoc/>
    public IDisposable Subscribe(Action<MemoryStoreChangedEvent> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        lock (_listenersLock)
        {
            _listeners = [.. _listeners, listener];
        }

        return new Subscription(this, listener);
    }

    /// <inheritdoc/>
    public ValueTask SetAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Key);

        var stored = entry with { CreatedAt = entry.CreatedAt ?? DateTimeOffset.UtcNow };
        var nsStore = _namespaces.GetOrAdd(entry.Namespace, static _ => new ConcurrentDictionary<string, MemoryEntry>(StringComparer.Ordinal));
        nsStore[entry.Key] = stored;
        Notify(new MemoryStoreChangedEvent(MemoryStoreChangeKind.Set, stored.Namespace, stored.Key, stored));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<MemoryEntry?> GetAsync(string ns, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_namespaces.TryGetValue(ns, out var nsStore) && nsStore.TryGetValue(key, out var entry))
        {
            return new ValueTask<MemoryEntry?>(entry);
        }

        return new ValueTask<MemoryEntry?>((MemoryEntry?)null);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(
        string ns,
        string? query = null,
        int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);

        if (!_namespaces.TryGetValue(ns, out var nsStore))
        {
            return new ValueTask<IReadOnlyList<MemoryEntry>>(Array.Empty<MemoryEntry>());
        }

        var results = string.IsNullOrEmpty(query)
            ? nsStore.Values.Take(maxResults).ToList()
            : nsStore.Values.Where(e => e.Content.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(maxResults).ToList();

        return new ValueTask<IReadOnlyList<MemoryEntry>>(results);
    }

    /// <inheritdoc/>
    public ValueTask DeleteAsync(string ns, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_namespaces.TryGetValue(ns, out var nsStore) && nsStore.TryRemove(key, out _))
        {
            Notify(new MemoryStoreChangedEvent(MemoryStoreChangeKind.Delete, ns, key));
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask ClearAsync(string ns, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);

        _namespaces.TryRemove(ns, out _);
        Notify(new MemoryStoreChangedEvent(MemoryStoreChangeKind.Clear, ns));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Returns the total number of entries across all namespaces. Useful for tests.
    /// </summary>
    public int Count => _namespaces.Values.Sum(static ns => ns.Count);

    private void Notify(MemoryStoreChangedEvent change)
    {
        var snapshot = Volatile.Read(ref _listeners);
        for (var i = 0; i < snapshot.Length; i++)
        {
            snapshot[i](change);
        }
    }

    private void Unsubscribe(Action<MemoryStoreChangedEvent> listener)
    {
        lock (_listenersLock)
        {
            var index = Array.IndexOf(_listeners, listener);
            if (index < 0)
            {
                return;
            }

            if (_listeners.Length == 1)
            {
                _listeners = [];
                return;
            }

            var updated = new Action<MemoryStoreChangedEvent>[_listeners.Length - 1];
            if (index > 0)
            {
                Array.Copy(_listeners, 0, updated, 0, index);
            }

            if (index < _listeners.Length - 1)
            {
                Array.Copy(_listeners, index + 1, updated, index, _listeners.Length - index - 1);
            }

            _listeners = updated;
        }
    }

    private sealed class Subscription(InMemoryMemoryStore owner, Action<MemoryStoreChangedEvent> listener) : IDisposable
    {
        private Action<MemoryStoreChangedEvent>? _listener = listener;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _listener, null);
            if (current is not null)
            {
                owner.Unsubscribe(current);
            }
        }
    }
}
