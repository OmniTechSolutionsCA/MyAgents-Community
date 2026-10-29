using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.InMemory;
using MyAgents.Core.Memory;

namespace MyAgents.Memory;

/// <summary>
/// Vector-backed implementation of <see cref="IVectorMemoryStore"/> that stores
/// <see cref="MemoryEntry"/> records with their embeddings in an
/// <see cref="InMemoryCollection{TKey,TRecord}"/> and supports semantic search.
/// </summary>
/// <remarks>
/// <para>Embedding model: any <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>
/// — swap to Azure OpenAI, Ollama, etc. at DI composition root.</para>
/// <para>v1 scope: InMemory vector provider only. One collection per namespace,
/// created on first access. No persistence across process restarts.</para>
/// </remarks>
public sealed class VectorMemoryStore : IVectorMemoryStore
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;

    // One SK InMemoryCollection per namespace, created lazily.
    private readonly ConcurrentDictionary<string, InMemoryCollection<string, VectorRecord>> _collections = new();

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
        var collection = GetOrCreateCollection(entry.Namespace);
        await collection.EnsureCollectionExistsAsync(cancellationToken).ConfigureAwait(false);

        var createdAtOffset = entry.CreatedAt ?? DateTimeOffset.UtcNow;
        var record = new VectorRecord
        {
            Key = entry.Key,
            Namespace = entry.Namespace,
            Content = entry.Content,
            Metadata = entry.Metadata,
            CreatedAtOffset = createdAtOffset,
            CreatedAt = createdAtOffset.ToString("O"),
            Embedding = embedding,
        };

        await collection.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<MemoryEntry?> GetAsync(string ns, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var collection = GetOrCreateCollection(ns);
        var record = await collection.GetAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
        return record is null ? null : ToEntry(record);
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
            // Delegate to vector search when a query is provided
            var vectorResults = await VectorSearchAsync(ns, query, maxResults, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var searchResults = new MemoryEntry[vectorResults.Count];
            for (var i = 0; i < vectorResults.Count; i++)
            {
                searchResults[i] = vectorResults[i].Entry;
            }
            return searchResults;
        }

        // No query — return all records in the namespace via GetAsync with filter
        var collection = GetOrCreateCollection(ns);
        var results = new List<MemoryEntry>();
        await foreach (var record in collection.GetAsync(
            filter: r => r.Namespace == ns,
            top: maxResults,
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            results.Add(ToEntry(record));
        }
        return results;
    }

    /// <inheritdoc/>
    public async ValueTask DeleteAsync(string ns, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var collection = GetOrCreateCollection(ns);
        await collection.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask ClearAsync(string ns, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);

        var collection = GetOrCreateCollection(ns);
        await collection.EnsureCollectionDeletedAsync(cancellationToken).ConfigureAwait(false);
        _collections.TryRemove(ns, out _);
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

        var queryEmbedding = await EmbedAsync(query, cancellationToken).ConfigureAwait(false);
        var collection = GetOrCreateCollection(ns);

        var results = new List<VectorSearchResult>(maxResults);
        await foreach (var r in collection.SearchAsync(
            queryEmbedding,
            top: maxResults,
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var score = (float)(r.Score ?? 0.0);
            if (score >= minScore)
            {
                results.Add(new VectorSearchResult(ToEntry(r.Record), score));
            }
        }

        return results;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private async ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken ct)
    {
        var result = await _embedder.GenerateAsync([text], cancellationToken: ct).ConfigureAwait(false);
        return result[0].Vector;
    }

    private InMemoryCollection<string, VectorRecord> GetOrCreateCollection(string ns)
    {
        return _collections.GetOrAdd(ns, name => new InMemoryCollection<string, VectorRecord>(name));
    }

    private static MemoryEntry ToEntry(VectorRecord record)
    {
        var metadata = record.Metadata;
        if (metadata is null && !string.IsNullOrEmpty(record.MetadataJson))
        {
            metadata = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(record.MetadataJson);
        }

        var createdAt = record.CreatedAtOffset;
        if (!createdAt.HasValue && !string.IsNullOrEmpty(record.CreatedAt) &&
            DateTimeOffset.TryParse(record.CreatedAt, out var parsed))
        {
            createdAt = parsed;
        }

        return new MemoryEntry(
            Key: record.Key,
            Namespace: record.Namespace,
            Content: record.Content,
            Metadata: metadata,
            CreatedAt: createdAt);
    }
}

// ── Internal record type ─────────────────────────────────────────────────────

/// <summary>
/// Internal record stored in the <see cref="InMemoryCollection{TKey,TRecord}"/>.
/// Uses <see cref="VectorStoreKey"/>, <see cref="VectorStoreData"/>, and
/// <see cref="VectorStoreVector"/> attributes for SK VectorData conventions.
/// </summary>
internal sealed class VectorRecord
{
    private string? _metadataJson;

    [VectorStoreKey]
    public string Key { get; set; } = string.Empty;

    [VectorStoreData]
    public string Namespace { get; set; } = string.Empty;

    [VectorStoreData]
    public string Content { get; set; } = string.Empty;

    [VectorStoreData]
    public string? MetadataJson
    {
        get => _metadataJson ??= (Metadata is null ? null : System.Text.Json.JsonSerializer.Serialize(Metadata));
        set => _metadataJson = value;
    }

    [VectorStoreData]
    public string? CreatedAt { get; set; }

    [VectorStoreVector(Dimensions: 1536)]
    public ReadOnlyMemory<float> Embedding { get; set; }

    /// <summary>
    /// Direct in-memory reference to metadata to bypass JSON serialization/deserialization.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; set; }

    /// <summary>
    /// Direct timestamp offset to bypass string parsing.
    /// </summary>
    public DateTimeOffset? CreatedAtOffset { get; set; }
}
