namespace MyAgents.Core.Memory;

/// <summary>
/// A single entry stored in an agent's memory.
/// </summary>
/// <param name="Key">Unique key within its namespace.</param>
/// <param name="Namespace">Logical grouping (e.g., "facts", "conversation", "user-prefs").</param>
/// <param name="Content">The textual content of this memory entry.</param>
/// <param name="Metadata">Optional freeform metadata (e.g., source, timestamp tag, relevance score).</param>
/// <param name="CreatedAt">UTC timestamp when the entry was created.</param>
public sealed record MemoryEntry(
    string Key,
    string Namespace,
    string Content,
    IReadOnlyDictionary<string, string>? Metadata = null,
    DateTimeOffset? CreatedAt = null);

/// <summary>
/// Abstraction for an agent memory store.
/// Implementations may be in-memory, vector-backed, Redis-backed, etc.
/// </summary>
public interface IMemoryStore
{
    /// <summary>
    /// Stores or replaces an entry identified by namespace + key.
    /// </summary>
    ValueTask SetAsync(MemoryEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves an entry by namespace and key, or returns null if not found.
    /// </summary>
    ValueTask<MemoryEntry?> GetAsync(string ns, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all entries in the given namespace whose content contains <paramref name="query"/> (case-insensitive).
    /// When <paramref name="query"/> is null or empty, returns all entries in the namespace.
    /// </summary>
    ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(
        string ns,
        string? query = null,
        int maxResults = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a specific entry. No-op if not found.
    /// </summary>
    ValueTask DeleteAsync(string ns, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all entries in the given namespace.
    /// </summary>
    ValueTask ClearAsync(string ns, CancellationToken cancellationToken = default);
}
