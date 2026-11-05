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

/// <summary>
/// Kind of mutation applied to an <see cref="IMemoryStore"/>.
/// </summary>
public enum MemoryStoreChangeKind
{
    /// <summary>An entry was added or updated.</summary>
    Set = 0,

    /// <summary>A specific entry was removed.</summary>
    Delete = 1,

    /// <summary>All entries in a namespace were cleared.</summary>
    Clear = 2,
}

/// <summary>
/// Describes a mutation that occurred in an <see cref="IMemoryStore"/>.
/// </summary>
/// <param name="Kind">The mutation kind (<see cref="MemoryStoreChangeKind.Set"/>, <see cref="MemoryStoreChangeKind.Delete"/>, or <see cref="MemoryStoreChangeKind.Clear"/>).</param>
/// <param name="Namespace">The affected namespace.</param>
/// <param name="Key">The affected entry key, or <see langword="null"/> when <paramref name="Kind"/> is <see cref="MemoryStoreChangeKind.Clear"/>.</param>
/// <param name="Entry">The stored entry when <paramref name="Kind"/> is <see cref="MemoryStoreChangeKind.Set"/>; otherwise <see langword="null"/>.</param>
public sealed record MemoryStoreChangedEvent(
    MemoryStoreChangeKind Kind,
    string Namespace,
    string? Key = null,
    MemoryEntry? Entry = null);

/// <summary>
/// Optional interface implemented by <see cref="IMemoryStore"/> implementations that publish
/// synchronous change notifications when entries are added, updated, deleted, or cleared.
/// Enables secondary indices (such as BM25 lexical indices) to stay synchronized automatically.
/// </summary>
public interface IMemoryStoreChangeNotifier
{
    /// <summary>
    /// Registers a listener invoked whenever an entry is stored, deleted, or a namespace is cleared.
    /// Disposing the returned token unregisters the listener.
    /// </summary>
    IDisposable Subscribe(Action<MemoryStoreChangedEvent> listener);
}
