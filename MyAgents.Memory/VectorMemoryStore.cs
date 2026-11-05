using System.Collections.Concurrent;
using System.Numerics.Tensors;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MyAgents.Core.Memory;

namespace MyAgents.Memory;

/// <summary>
/// Vector-backed implementation of <see cref="IVectorMemoryStore"/> that stores
/// <see cref="MemoryEntry"/> records with their embeddings in memory, supports
/// high-performance semantic search powered by SIMD-accelerated <see cref="TensorPrimitives"/>,
/// and provides durable snapshot serialization to and from disk or streams.
/// </summary>
public sealed class VectorMemoryStore : IVectorMemoryStore, IMemoryStoreChangeNotifier
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly ConcurrentDictionary<string, NamespaceCollection> _collections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _listenersLock = new();
    private Action<MemoryStoreChangedEvent>[] _listeners = [];

    /// <summary>
    /// Creates a new <see cref="VectorMemoryStore"/>.
    /// </summary>
    /// <param name="embedder">
    /// Embedding generator used to convert text to float vectors.
    /// The embedding dimension must be consistent across all calls.
    /// </param>
    public VectorMemoryStore(IEmbeddingGenerator<string, Embedding<float>> embedder)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
    }

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

    // ── IMemoryStore ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask SetAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Key);

        var collection = _collections.GetOrAdd(entry.Namespace, static _ => new NamespaceCollection());

        ReadOnlyMemory<float> normalizedEmbedding;
        if (collection.Entries.TryGetValue(entry.Key, out var existingRecord) &&
            string.Equals(existingRecord.Content, entry.Content, StringComparison.Ordinal))
        {
            normalizedEmbedding = existingRecord.Embedding;
        }
        else
        {
            var embedding = await EmbedAsync(entry.Content, cancellationToken).ConfigureAwait(false);
            normalizedEmbedding = Normalize(embedding);
        }

        var record = new MemoryEntryRecord
        {
            Key = entry.Key,
            Namespace = entry.Namespace,
            Content = entry.Content,
            Metadata = entry.Metadata,
            CreatedAt = entry.CreatedAt ?? DateTimeOffset.UtcNow,
            Embedding = normalizedEmbedding,
        };

        collection.Entries[entry.Key] = record;
        var storedEntry = record.ToEntry();
        Notify(new MemoryStoreChangedEvent(MemoryStoreChangeKind.Set, storedEntry.Namespace, storedEntry.Key, storedEntry));
    }

    /// <inheritdoc/>
    public async ValueTask SetBatchAsync(
        IReadOnlyList<MemoryEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        var resolvedEmbeddings = new ReadOnlyMemory<float>[entries.Count];
        var pendingTexts = new List<string>(entries.Count);
        var pendingIndices = new List<int>(entries.Count);

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Namespace);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Key);

            if (_collections.TryGetValue(entry.Namespace, out var existingCollection) &&
                existingCollection.Entries.TryGetValue(entry.Key, out var existingRecord) &&
                string.Equals(existingRecord.Content, entry.Content, StringComparison.Ordinal))
            {
                resolvedEmbeddings[i] = existingRecord.Embedding;
            }
            else
            {
                pendingTexts.Add(entry.Content);
                pendingIndices.Add(i);
            }
        }

        if (pendingTexts.Count > 0)
        {
            var generated = await _embedder.GenerateAsync(pendingTexts, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (generated.Count != pendingTexts.Count)
            {
                throw new InvalidOperationException(
                    $"Embedding generator returned {generated.Count} embeddings for a batch of {pendingTexts.Count} entries.");
            }

            for (var p = 0; p < pendingIndices.Count; p++)
            {
                resolvedEmbeddings[pendingIndices[p]] = Normalize(generated[p].Vector);
            }
        }

        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var collection = _collections.GetOrAdd(entry.Namespace, static _ => new NamespaceCollection());
            var record = new MemoryEntryRecord
            {
                Key = entry.Key,
                Namespace = entry.Namespace,
                Content = entry.Content,
                Metadata = entry.Metadata,
                CreatedAt = entry.CreatedAt ?? now,
                Embedding = resolvedEmbeddings[i],
            };
            collection.Entries[entry.Key] = record;
            var storedEntry = record.ToEntry();
            Notify(new MemoryStoreChangedEvent(MemoryStoreChangeKind.Set, storedEntry.Namespace, storedEntry.Key, storedEntry));
        }
    }

    /// <inheritdoc/>
    public ValueTask<MemoryEntry?> GetAsync(string ns, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_collections.TryGetValue(ns, out var collection) &&
            collection.Entries.TryGetValue(key, out var record))
        {
            return new ValueTask<MemoryEntry?>(record.ToEntry());
        }

        return new ValueTask<MemoryEntry?>((MemoryEntry?)null);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(
        string ns,
        string? query = null,
        int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var vectorResults = await VectorSearchAsync(ns, query, maxResults, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var results = new MemoryEntry[vectorResults.Count];
            for (var i = 0; i < vectorResults.Count; i++)
            {
                results[i] = vectorResults[i].Entry;
            }
            return results;
        }

        if (!_collections.TryGetValue(ns, out var collection))
        {
            return Array.Empty<MemoryEntry>();
        }

        var unranked = new List<MemoryEntry>(Math.Min(maxResults, collection.Entries.Count));
        foreach (var record in collection.Entries.Values)
        {
            unranked.Add(record.ToEntry());
            if (unranked.Count >= maxResults)
            {
                break;
            }
        }
        return unranked;
    }

    /// <inheritdoc/>
    public ValueTask DeleteAsync(string ns, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_collections.TryGetValue(ns, out var collection) && collection.Entries.TryRemove(key, out _))
        {
            Notify(new MemoryStoreChangedEvent(MemoryStoreChangeKind.Delete, ns, key));
        }

        return default;
    }

    /// <inheritdoc/>
    public ValueTask ClearAsync(string ns, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);

        _collections.TryRemove(ns, out _);
        Notify(new MemoryStoreChangedEvent(MemoryStoreChangeKind.Clear, ns));
        return default;
    }

    // ── IVectorMemoryStore ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<VectorSearchResult>> VectorSearchAsync(
        string ns,
        string query,
        int maxResults = 5,
        float minScore = 0.0f,
        IReadOnlyDictionary<string, string>? metadataFilters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        if (!_collections.TryGetValue(ns, out var collection) || collection.Entries.IsEmpty)
        {
            return Array.Empty<VectorSearchResult>();
        }

        var queryEmbedding = await EmbedAsync(query, cancellationToken).ConfigureAwait(false);
        var normalizedQuery = Normalize(queryEmbedding);
        var querySpan = normalizedQuery.Span;
        var hasFilters = metadataFilters is { Count: > 0 };

        var topQueue = new PriorityQueue<MemoryEntryRecord, float>(maxResults);

        foreach (var record in collection.Entries.Values)
        {
            if (hasFilters && !MatchesMetadata(record.Metadata, metadataFilters))
            {
                continue;
            }

            var similarity = TensorPrimitives.Dot(querySpan, record.Embedding.Span);
            if (similarity < minScore)
            {
                continue;
            }

            if (topQueue.Count < maxResults)
            {
                topQueue.Enqueue(record, similarity);
            }
            else
            {
                topQueue.TryPeek(out _, out var lowestScore);
                if (similarity > lowestScore)
                {
                    topQueue.DequeueEnqueue(record, similarity);
                }
            }
        }

        if (topQueue.Count == 0)
        {
            return Array.Empty<VectorSearchResult>();
        }

        var results = new VectorSearchResult[topQueue.Count];
        for (var i = results.Length - 1; i >= 0; i--)
        {
            topQueue.TryDequeue(out var record, out var score);
            results[i] = new VectorSearchResult(record!.ToEntry(), score);
        }

        return results;
    }

    // ── Durable Snapshot Persistence ──────────────────────────────────────────

    /// <summary>
    /// Serializes all stored entries and their precomputed embeddings to <paramref name="destination"/>.
    /// </summary>
    public async ValueTask SaveSnapshotAsync(
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        await using var writer = new Utf8JsonWriter(destination);
        writer.WriteStartObject();
        writer.WriteNumber("version", 1);
        writer.WriteStartArray("entries");

        foreach (var (_, collection) in _collections)
        {
            foreach (var record in collection.Entries.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteStartObject();
                writer.WriteString("key", record.Key);
                writer.WriteString("namespace", record.Namespace);
                writer.WriteString("content", record.Content);
                if (record.CreatedAt is { } createdAt)
                {
                    writer.WriteString("createdAt", createdAt.ToString("O"));
                }

                if (record.Metadata is { Count: > 0 } metadata)
                {
                    writer.WriteStartObject("metadata");
                    foreach (var (mk, mv) in metadata)
                    {
                        writer.WriteString(mk, mv);
                    }
                    writer.WriteEndObject();
                }

                writer.WriteStartArray("embedding");
                var span = record.Embedding.Span;
                for (var i = 0; i < span.Length; i++)
                {
                    writer.WriteNumberValue(span[i]);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Restores entries and their precomputed embeddings from <paramref name="source"/> without re-invoking the embedder.
    /// </summary>
    /// <returns>The number of entries loaded from the snapshot.</returns>
    public async ValueTask<int> LoadSnapshotAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var doc = await JsonDocument.ParseAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("entries", out var entriesElement) ||
            entriesElement.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var loaded = 0;
        foreach (var item in entriesElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = item.GetProperty("key").GetString();
            var ns = item.GetProperty("namespace").GetString();
            var content = item.GetProperty("content").GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(ns))
            {
                continue;
            }

            DateTimeOffset? createdAt = null;
            if (item.TryGetProperty("createdAt", out var createdProp) &&
                createdProp.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(createdProp.GetString(), out var parsedCreated))
            {
                createdAt = parsedCreated;
            }

            Dictionary<string, string>? metadata = null;
            if (item.TryGetProperty("metadata", out var metaProp) &&
                metaProp.ValueKind == JsonValueKind.Object)
            {
                metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in metaProp.EnumerateObject())
                {
                    if (prop.Value.GetString() is { } val)
                    {
                        metadata[prop.Name] = val;
                    }
                }
            }

            var embProp = item.GetProperty("embedding");
            var vector = new float[embProp.GetArrayLength()];
            var idx = 0;
            foreach (var num in embProp.EnumerateArray())
            {
                vector[idx++] = num.GetSingle();
            }

            var collection = _collections.GetOrAdd(ns, static _ => new NamespaceCollection());
            var record = new MemoryEntryRecord
            {
                Key = key,
                Namespace = ns,
                Content = content,
                Metadata = metadata,
                CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
                Embedding = Normalize(vector),
            };
            collection.Entries[key] = record;
            var storedEntry = record.ToEntry();
            Notify(new MemoryStoreChangedEvent(MemoryStoreChangeKind.Set, storedEntry.Namespace, storedEntry.Key, storedEntry));
            loaded++;
        }

        return loaded;
    }

    /// <summary>
    /// Atomically saves a snapshot of all stored vector entries to <paramref name="filePath"/>.
    /// </summary>
    public async ValueTask SaveToFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await SaveSnapshotAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>
    /// Loads stored vector entries from <paramref name="filePath"/> if the file exists.
    /// </summary>
    /// <returns>The number of entries loaded, or 0 if the file does not exist.</returns>
    public async ValueTask<int> LoadFromFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            return 0;
        }

        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await LoadSnapshotAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    private static bool MatchesMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        IReadOnlyDictionary<string, string>? filters)
    {
        if (filters is null || filters.Count == 0)
        {
            return true;
        }

        if (metadata is null)
        {
            return false;
        }

        foreach (var (key, value) in filters)
        {
            if (!metadata.TryGetValue(key, out var existing) ||
                !string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private async ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken ct)
    {
        var result = await _embedder.GenerateAsync([text], cancellationToken: ct).ConfigureAwait(false);
        return result[0].Vector;
    }

    private static ReadOnlyMemory<float> Normalize(ReadOnlyMemory<float> vector)
    {
        var span = vector.Span;
        var sumSquares = TensorPrimitives.SumOfSquares(span);
        if (sumSquares <= float.Epsilon || MathF.Abs(sumSquares - 1.0f) < 1e-5f)
        {
            return vector;
        }

        var norm = MathF.Sqrt(sumSquares);
        var normalized = new float[span.Length];
        TensorPrimitives.Divide(span, norm, normalized.AsSpan());
        return normalized;
    }

    private sealed class NamespaceCollection
    {
        public readonly ConcurrentDictionary<string, MemoryEntryRecord> Entries = new(StringComparer.Ordinal);
    }

    private sealed class MemoryEntryRecord
    {
        public required string Key { get; init; }
        public required string Namespace { get; init; }
        public required string Content { get; init; }
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }
        public DateTimeOffset? CreatedAt { get; init; }
        public ReadOnlyMemory<float> Embedding { get; init; }

        public MemoryEntry ToEntry() => new(
            Key: Key,
            Namespace: Namespace,
            Content: Content,
            Metadata: Metadata,
            CreatedAt: CreatedAt);
    }

    private sealed class Subscription(VectorMemoryStore owner, Action<MemoryStoreChangedEvent> listener) : IDisposable
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
