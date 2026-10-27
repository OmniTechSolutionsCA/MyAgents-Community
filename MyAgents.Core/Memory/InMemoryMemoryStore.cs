using System.Collections.Concurrent;

namespace MyAgents.Core.Memory;

/// <summary>
/// Thread-safe, in-process implementation of <see cref="IMemoryStore"/>.
/// Entries are partitioned by namespace in a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// for O(1) lookups and isolated namespace searches.
/// Suitable for single-process agents and tests. For multi-process or persistent memory,
/// replace with a vector-store or Redis-backed implementation behind the same interface.
/// </summary>
public sealed class InMemoryMemoryStore : IMemoryStore
{
    // Namespace -> (Key -> MemoryEntry)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, MemoryEntry>> _namespaces = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public ValueTask SetAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Key);

        var stored = entry with { CreatedAt = entry.CreatedAt ?? DateTimeOffset.UtcNow };
        var nsStore = _namespaces.GetOrAdd(entry.Namespace, static _ => new ConcurrentDictionary<string, MemoryEntry>(StringComparer.Ordinal));
        nsStore[entry.Key] = stored;
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

        if (_namespaces.TryGetValue(ns, out var nsStore))
        {
            nsStore.TryRemove(key, out _);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask ClearAsync(string ns, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);

        _namespaces.TryRemove(ns, out _);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Returns the total number of entries across all namespaces. Useful for tests.
    /// </summary>
    public int Count => _namespaces.Values.Sum(static ns => ns.Count);
}
