using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics.Tensors;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using MyAgents.Core.Memory;

namespace MyAgents.Memory;

/// <summary>
/// Canonical record model used when persisting <see cref="MemoryEntry"/> items to a
/// <see cref="Microsoft.Extensions.VectorData.VectorStore"/> or <see cref="VectorStoreCollection{TKey, TRecord}"/>.
/// </summary>
public sealed class VectorDataMemoryRecord
{
    /// <summary>
    /// Composite storage key (<c>"{Namespace}|{Key}"</c>) or item key.
    /// </summary>
    [VectorStoreKey]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Logical memory namespace.
    /// </summary>
    [VectorStoreData(IsIndexed = true)]
    public string Namespace { get; set; } = string.Empty;

    /// <summary>
    /// Entry key within <see cref="Namespace"/>.
    /// </summary>
    [VectorStoreData(IsIndexed = true)]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Text content of the memory entry.
    /// </summary>
    [VectorStoreData(IsFullTextIndexed = true)]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Optional JSON-serialized metadata dictionary.
    /// </summary>
    [VectorStoreData]
    public string? MetadataJson { get; set; }

    /// <summary>
    /// ISO-8601 round-trip creation timestamp.
    /// </summary>
    [VectorStoreData]
    public string? CreatedAtIso { get; set; }

    /// <summary>
    /// Dense float embedding vector.
    /// </summary>
    [VectorStoreVector(1536, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Embedding { get; set; }

    /// <summary>
    /// Converts this vector-store record back to a <see cref="MemoryEntry"/>.
    /// </summary>
    public MemoryEntry ToMemoryEntry()
    {
        Dictionary<string, string>? metadata = null;
        if (!string.IsNullOrWhiteSpace(MetadataJson))
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(MetadataJson);
            if (parsed is not null)
            {
                metadata = new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
            }
        }

        DateTimeOffset? createdAt = null;
        if (!string.IsNullOrWhiteSpace(CreatedAtIso) &&
            DateTimeOffset.TryParse(CreatedAtIso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
        {
            createdAt = dt;
        }

        return new MemoryEntry(
            Key: Key,
            Namespace: Namespace,
            Content: Content,
            Metadata: metadata,
            CreatedAt: createdAt);
    }

    /// <summary>
    /// Creates a <see cref="VectorDataMemoryRecord"/> from a <see cref="MemoryEntry"/> and its embedding vector.
    /// </summary>
    public static VectorDataMemoryRecord FromMemoryEntry(MemoryEntry entry, ReadOnlyMemory<float> embedding)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new VectorDataMemoryRecord
        {
            Id = ComposeId(entry.Namespace, entry.Key),
            Namespace = entry.Namespace,
            Key = entry.Key,
            Content = entry.Content,
            MetadataJson = entry.Metadata is { Count: > 0 } ? JsonSerializer.Serialize(entry.Metadata) : null,
            CreatedAtIso = (entry.CreatedAt ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture),
            Embedding = embedding,
        };
    }

    internal static string ComposeId(string ns, string key) => $"{ns}|{key}";
}

/// <summary>
/// Adapter that implements <see cref="IVectorMemoryStore"/> and <see cref="IMemoryStoreChangeNotifier"/>
/// over any <see cref="Microsoft.Extensions.VectorData.VectorStore"/> or
/// <see cref="VectorStoreCollection{TKey, TRecord}"/> (such as Qdrant, pgvector, Azure AI Search, SQLite-vec, or Redis)
/// without coupling <c>MyAgents.Memory</c> to any vendor SDK.
/// </summary>
public sealed class VectorDataMemoryStore : IVectorMemoryStore, IMemoryStoreChangeNotifier, IDisposable
{
    private readonly VectorStoreCollection<string, VectorDataMemoryRecord> _collection;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly bool _ownsCollection;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _knownKeysByNamespace = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _listenersLock = new();
    private Action<MemoryStoreChangedEvent>[] _listeners = [];
    private volatile bool _collectionInitialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>
    /// Creates a new <see cref="VectorDataMemoryStore"/> wrapping an existing <see cref="VectorStoreCollection{TKey, TRecord}"/>.
    /// </summary>
    /// <param name="collection">The underlying Microsoft.Extensions.VectorData collection.</param>
    /// <param name="embedder">Embedding generator used to compute query and document vectors.</param>
    /// <param name="ownsCollection">Whether disposing this store should also dispose <paramref name="collection"/>.</param>
    public VectorDataMemoryStore(
        VectorStoreCollection<string, VectorDataMemoryRecord> collection,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        bool ownsCollection = false)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(embedder);
        _collection = collection;
        _embedder = embedder;
        _ownsCollection = ownsCollection;
    }

    /// <summary>
    /// Creates a new <see cref="VectorDataMemoryStore"/> from a <see cref="VectorStore"/> and collection name.
    /// </summary>
    /// <param name="vectorStore">The underlying vector store provider.</param>
    /// <param name="embedder">Embedding generator used to compute query and document vectors.</param>
    /// <param name="collectionName">Name of the collection in <paramref name="vectorStore"/>.</param>
    /// <param name="dimensions">Vector dimension count (default: 1536).</param>
    public VectorDataMemoryStore(
        VectorStore vectorStore,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        string collectionName = "myagents_memory",
        int dimensions = 1536)
    {
        ArgumentNullException.ThrowIfNull(vectorStore);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dimensions, 0);

        var definition = new VectorStoreCollectionDefinition
        {
            Properties =
            [
                new VectorStoreKeyProperty(nameof(VectorDataMemoryRecord.Id), typeof(string)),
                new VectorStoreDataProperty(nameof(VectorDataMemoryRecord.Namespace), typeof(string)) { IsIndexed = true },
                new VectorStoreDataProperty(nameof(VectorDataMemoryRecord.Key), typeof(string)) { IsIndexed = true },
                new VectorStoreDataProperty(nameof(VectorDataMemoryRecord.Content), typeof(string)) { IsFullTextIndexed = true },
                new VectorStoreDataProperty(nameof(VectorDataMemoryRecord.MetadataJson), typeof(string)),
                new VectorStoreDataProperty(nameof(VectorDataMemoryRecord.CreatedAtIso), typeof(string)),
                new VectorStoreVectorProperty(nameof(VectorDataMemoryRecord.Embedding), typeof(ReadOnlyMemory<float>), dimensions)
                {
                    DistanceFunction = DistanceFunction.CosineSimilarity,
                },
            ],
        };

        _collection = vectorStore.GetCollection<string, VectorDataMemoryRecord>(collectionName, definition);
        _embedder = embedder;
        _ownsCollection = true;
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Action<MemoryStoreChangedEvent> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        lock (_listenersLock)
        {
            _listeners = [.. _listeners, listener];
        }

        return new Subscription(this, listener);
    }

    /// <inheritdoc />
    public async ValueTask SetAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Key);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var id = VectorDataMemoryRecord.ComposeId(entry.Namespace, entry.Key);
        var existing = await _collection.GetAsync(
            id,
            new RecordRetrievalOptions { IncludeVectors = true },
            cancellationToken).ConfigureAwait(false);

        ReadOnlyMemory<float> normalizedEmbedding;
        if (existing is not null &&
            !existing.Embedding.IsEmpty &&
            string.Equals(existing.Content, entry.Content, StringComparison.Ordinal))
        {
            normalizedEmbedding = existing.Embedding;
        }
        else
        {
            var generated = await _embedder.GenerateAsync([entry.Content], cancellationToken: cancellationToken).ConfigureAwait(false);
            normalizedEmbedding = Normalize(generated[0].Vector);
        }

        var record = VectorDataMemoryRecord.FromMemoryEntry(entry, normalizedEmbedding);
        await _collection.UpsertAsync(record, cancellationToken).ConfigureAwait(false);

        TrackKey(entry.Namespace, entry.Key);
        var storedEntry = record.ToMemoryEntry();
        Notify(new MemoryStoreChangedEvent(MemoryStoreChangeKind.Set, storedEntry.Namespace, storedEntry.Key, storedEntry));
    }

    /// <inheritdoc />
    public async ValueTask SetBatchAsync(
        IReadOnlyList<MemoryEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var ids = new string[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Namespace);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Key);
            ids[i] = VectorDataMemoryRecord.ComposeId(entry.Namespace, entry.Key);
        }

        var existingById = new Dictionary<string, VectorDataMemoryRecord>(StringComparer.Ordinal);
        await foreach (var existing in _collection.GetAsync(
            ids,
            new RecordRetrievalOptions { IncludeVectors = true },
            cancellationToken).ConfigureAwait(false))
        {
            if (existing is not null)
            {
                existingById[existing.Id] = existing;
            }
        }

        var resolvedEmbeddings = new ReadOnlyMemory<float>[entries.Count];
        var pendingTexts = new List<string>(entries.Count);
        var pendingIndices = new List<int>(entries.Count);

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (existingById.TryGetValue(ids[i], out var existing) &&
                !existing.Embedding.IsEmpty &&
                string.Equals(existing.Content, entry.Content, StringComparison.Ordinal))
            {
                resolvedEmbeddings[i] = existing.Embedding;
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

        var records = new VectorDataMemoryRecord[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            records[i] = VectorDataMemoryRecord.FromMemoryEntry(entries[i], resolvedEmbeddings[i]);
        }

        await _collection.UpsertAsync(records, cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < records.Length; i++)
        {
            var rec = records[i];
            TrackKey(rec.Namespace, rec.Key);
            var storedEntry = rec.ToMemoryEntry();
            Notify(new MemoryStoreChangedEvent(MemoryStoreChangeKind.Set, storedEntry.Namespace, storedEntry.Key, storedEntry));
        }
    }

    /// <inheritdoc />
    public async ValueTask<MemoryEntry?> GetAsync(string ns, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var id = VectorDataMemoryRecord.ComposeId(ns, key);
        var record = await _collection.GetAsync(id, cancellationToken: cancellationToken).ConfigureAwait(false);
        return record?.ToMemoryEntry();
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(
        string ns,
        string? query = null,
        int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var vectorHits = await VectorSearchAsync(ns, query, maxResults, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var list = new MemoryEntry[vectorHits.Count];
            for (var i = 0; i < vectorHits.Count; i++)
            {
                list[i] = vectorHits[i].Entry;
            }
            return list;
        }

        var results = new List<MemoryEntry>();
        var fetchLimit = maxResults == int.MaxValue ? 10_000 : Math.Max(1, maxResults);
        await foreach (var record in _collection.GetAsync(
            r => r.Namespace == ns,
            fetchLimit,
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            TrackKey(record.Namespace, record.Key);
            results.Add(record.ToMemoryEntry());
            if (results.Count >= maxResults)
            {
                break;
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(string ns, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var id = VectorDataMemoryRecord.ComposeId(ns, key);
        await _collection.DeleteAsync(id, cancellationToken).ConfigureAwait(false);

        if (_knownKeysByNamespace.TryGetValue(ns, out var keys))
        {
            keys.TryRemove(key, out _);
        }

        Notify(new MemoryStoreChangedEvent(MemoryStoreChangeKind.Delete, ns, key));
    }

    /// <inheritdoc />
    public async ValueTask ClearAsync(string ns, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var idsToDelete = new List<string>();
        if (_knownKeysByNamespace.TryRemove(ns, out var tracked))
        {
            foreach (var key in tracked.Keys)
            {
                idsToDelete.Add(VectorDataMemoryRecord.ComposeId(ns, key));
            }
        }

        await foreach (var record in _collection.GetAsync(
            r => r.Namespace == ns,
            top: 10_000,
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var id = record.Id;
            if (!idsToDelete.Contains(id))
            {
                idsToDelete.Add(id);
            }
        }

        if (idsToDelete.Count > 0)
        {
            await _collection.DeleteAsync(idsToDelete, cancellationToken).ConfigureAwait(false);
        }

        Notify(new MemoryStoreChangedEvent(MemoryStoreChangeKind.Clear, ns));
    }

    /// <inheritdoc />
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

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var generated = await _embedder.GenerateAsync([query], cancellationToken: cancellationToken).ConfigureAwait(false);
        var queryEmbedding = Normalize(generated[0].Vector);

        var hasMetadataFilters = metadataFilters is { Count: > 0 };
        var providerTop = hasMetadataFilters ? Math.Max(maxResults * 4, 32) : maxResults;

        var searchOptions = new VectorSearchOptions<VectorDataMemoryRecord>
        {
            Filter = r => r.Namespace == ns,
        };

        var results = new List<VectorSearchResult>(maxResults);
        await foreach (var hit in _collection.SearchAsync(
            queryEmbedding,
            top: providerTop,
            options: searchOptions,
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var score = (float)(hit.Score ?? 0.0);
            if (score < minScore)
            {
                continue;
            }

            var entry = hit.Record.ToMemoryEntry();
            if (hasMetadataFilters && !MatchesMetadata(entry.Metadata, metadataFilters))
            {
                continue;
            }

            results.Add(new VectorSearchResult(entry, score));
            if (results.Count >= maxResults)
            {
                break;
            }
        }

        return results;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _initLock.Dispose();
        if (_ownsCollection)
        {
            _collection.Dispose();
        }
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_collectionInitialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_collectionInitialized)
            {
                await _collection.EnsureCollectionExistsAsync(cancellationToken).ConfigureAwait(false);
                _collectionInitialized = true;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    private void TrackKey(string ns, string key)
    {
        var set = _knownKeysByNamespace.GetOrAdd(ns, static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        set[key] = 0;
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

    private sealed class Subscription(VectorDataMemoryStore owner, Action<MemoryStoreChangedEvent> listener) : IDisposable
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
