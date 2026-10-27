using System.Collections.Concurrent;

namespace MyAgents.Core.Memory;

/// <summary>
/// Thread-safe, in-process implementation of <see cref="IMemoryStore"/>.
/// Entries are keyed by (namespace, key) and stored in a <see cref="ConcurrentDictionary"/>.
/// Suitable for single-process agents and tests. For multi-process or persistent memory,
/// replace with a vector-store or Redis-backed implementation behind the same interface.
/// </summary>
public sealed class InMemoryMemoryStore : IMemoryStore
{
    // Key: (namespace, key) tuple for O(1) exact lookups
    private readonly ConcurrentDictionary<(string Ns, string Key), MemoryEntry> _store = new();

    /// <inheritdoc/>
    public ValueTask SetAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Key);

        var stored = entry with { CreatedAt = entry.CreatedAt ?? DateTimeOffset.UtcNow };
        _store[(entry.Namespace, entry.Key)] = stored;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<MemoryEntry?> GetAsync(string ns, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _store.TryGetValue((ns, key), out var entry);
        return new ValueTask<MemoryEntry?>(entry);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(
        string ns,
        string? query = null,
        int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);

        var results = _store.Values
            .Where(e => e.Namespace == ns)
            .Where(e => string.IsNullOrEmpty(query) ||
                        e.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults)
            .ToList();

        return new ValueTask<IReadOnlyList<MemoryEntry>>(results);
    }

    /// <inheritdoc/>
    public ValueTask DeleteAsync(string ns, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _store.TryRemove((ns, key), out _);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask ClearAsync(string ns, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);

        foreach (var k in _store.Keys.Where(k => k.Ns == ns).ToList())
            _store.TryRemove(k, out _);

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Returns the total number of entries across all namespaces. Useful for tests.
    /// </summary>
    public int Count => _store.Count;
}
