using System.Collections.Concurrent;
using System.Numerics.Tensors;
using Microsoft.Extensions.AI;
using MyAgents.Core.Memory;

namespace MyAgents.Memory;

/// <summary>
/// Vector-backed implementation of <see cref="IVectorMemoryStore"/> that stores
/// <see cref="MemoryEntry"/> records with their embeddings in memory and supports
/// high-performance semantic search powered by SIMD-accelerated <see cref="TensorPrimitives"/>.
/// </summary>
public sealed class VectorMemoryStore : IVectorMemoryStore
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly ConcurrentDictionary<string, NamespaceCollection> _collections = new(StringComparer.OrdinalIgnoreCase);

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

    // ── IMemoryStore ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask SetAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Key);

        var embedding = await EmbedAsync(entry.Content, cancellationToken).ConfigureAwait(false);
        var collection = _collections.GetOrAdd(entry.Namespace, static _ => new NamespaceCollection());

        var record = new MemoryEntryRecord
        {
            Key = entry.Key,
            Namespace = entry.Namespace,
            Content = entry.Content,
            Metadata = entry.Metadata,
            CreatedAt = entry.CreatedAt ?? DateTimeOffset.UtcNow,
            Embedding = embedding,
        };

        collection.Entries[entry.Key] = record;
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

        if (_collections.TryGetValue(ns, out var collection))
        {
            collection.Entries.TryRemove(key, out _);
        }

        return default;
    }

    /// <inheritdoc/>
    public ValueTask ClearAsync(string ns, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);

        _collections.TryRemove(ns, out _);
        return default;
    }

    // ── IVectorMemoryStore ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<VectorSearchResult>> VectorSearchAsync(
        string ns,
        string query,
        int maxResults = 5,
        float minScore = 0.0f,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        if (!_collections.TryGetValue(ns, out var collection) || collection.Entries.IsEmpty)
        {
            return Array.Empty<VectorSearchResult>();
        }

        var queryEmbedding = await EmbedAsync(query, cancellationToken).ConfigureAwait(false);
        var querySpan = queryEmbedding.Span;

        var topQueue = new PriorityQueue<MemoryEntryRecord, float>(maxResults);

        foreach (var record in collection.Entries.Values)
        {
            var similarity = TensorPrimitives.CosineSimilarity(querySpan, record.Embedding.Span);
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

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private async ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken ct)
    {
        var result = await _embedder.GenerateAsync([text], cancellationToken: ct).ConfigureAwait(false);
        return result[0].Vector;
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
}
