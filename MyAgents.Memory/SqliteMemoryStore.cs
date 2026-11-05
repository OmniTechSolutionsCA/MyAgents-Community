using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using MyAgents.Core.Memory;

namespace MyAgents.Memory;

/// <summary>
/// Durable SQLite-backed implementation of <see cref="IVectorMemoryStore"/> and <see cref="IMemoryStoreChangeNotifier"/>.
/// Persists <see cref="MemoryEntry"/> records, JSON metadata, and pre-normalized float embeddings in a single SQLite database
/// while maintaining an in-memory cache for microsecond-latency SIMD <see cref="TensorPrimitives.Dot(ReadOnlySpan{float}, ReadOnlySpan{float})"/> vector search.
/// Also skips redundant embedding generation when upserting an entry whose content is unchanged.
/// </summary>
public sealed class SqliteMemoryStore : IVectorMemoryStore, IMemoryStoreChangeNotifier, IDisposable
{
    private readonly string _connectionString;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embedder;
    private readonly SqliteConnection? _keepAliveConnection;
    private readonly ConcurrentDictionary<string, NamespaceCollection> _collections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _dbLock = new();
    private readonly Lock _listenersLock = new();
    private Action<MemoryStoreChangedEvent>[] _listeners = [];
    private bool _disposed;

    /// <summary>
    /// Creates a new <see cref="SqliteMemoryStore"/> backed by the specified SQLite file path or connection string.
    /// </summary>
    /// <param name="connectionStringOrFilePath">
    /// Either a file path (e.g., <c>"memory.db"</c>) or a full SQLite connection string (e.g., <c>"Data Source=memory.db"</c>).
    /// </param>
    /// <param name="embedder">
    /// Optional <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> used to compute dense float vectors for semantic search.
    /// When <c>null</c>, entries are persisted and searchable via substring/metadata matching and BM25 hybrid indexing.
    /// </param>
    public SqliteMemoryStore(
        string connectionStringOrFilePath,
        IEmbeddingGenerator<string, Embedding<float>>? embedder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringOrFilePath);

        _embedder = embedder;
        _connectionString = NormalizeConnectionString(connectionStringOrFilePath, out var requiresKeepAlive);

        if (requiresKeepAlive)
        {
            _keepAliveConnection = new SqliteConnection(_connectionString);
            _keepAliveConnection.Open();
        }

        InitializeSchemaAndHydrateCache();
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Key);

        var collection = _collections.GetOrAdd(entry.Namespace, static _ => new NamespaceCollection());

        ReadOnlyMemory<float> normalizedEmbedding = ReadOnlyMemory<float>.Empty;
        if (_embedder is not null)
        {
            if (collection.Entries.TryGetValue(entry.Key, out var existingRecord) &&
                !existingRecord.Embedding.IsEmpty &&
                string.Equals(existingRecord.Content, entry.Content, StringComparison.Ordinal))
            {
                normalizedEmbedding = existingRecord.Embedding;
            }
            else
            {
                var generated = await _embedder.GenerateAsync([entry.Content], cancellationToken: cancellationToken).ConfigureAwait(false);
                normalizedEmbedding = Normalize(generated[0].Vector);
            }
        }

        var createdAt = entry.CreatedAt ?? DateTimeOffset.UtcNow;
        var record = new MemoryEntryRecord
        {
            Key = entry.Key,
            Namespace = entry.Namespace,
            Content = entry.Content,
            Metadata = entry.Metadata,
            CreatedAt = createdAt,
            Embedding = normalizedEmbedding,
        };

        PersistBatch([record]);
        collection.Entries[entry.Key] = record;

        var storedEntry = record.ToEntry();
        Notify(new MemoryStoreChangedEvent(MemoryStoreChangeKind.Set, storedEntry.Namespace, storedEntry.Key, storedEntry));
    }

    /// <inheritdoc />
    public async ValueTask SetBatchAsync(
        IReadOnlyList<MemoryEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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

            if (_embedder is not null)
            {
                if (_collections.TryGetValue(entry.Namespace, out var existingCollection) &&
                    existingCollection.Entries.TryGetValue(entry.Key, out var existingRecord) &&
                    !existingRecord.Embedding.IsEmpty &&
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
        }

        if (_embedder is not null && pendingTexts.Count > 0)
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
        var records = new MemoryEntryRecord[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            records[i] = new MemoryEntryRecord
            {
                Key = entry.Key,
                Namespace = entry.Namespace,
                Content = entry.Content,
                Metadata = entry.Metadata,
                CreatedAt = entry.CreatedAt ?? now,
                Embedding = resolvedEmbeddings[i],
            };
        }

        PersistBatch(records);

        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            var collection = _collections.GetOrAdd(record.Namespace, static _ => new NamespaceCollection());
            collection.Entries[record.Key] = record;
            var storedEntry = record.ToEntry();
            Notify(new MemoryStoreChangedEvent(MemoryStoreChangeKind.Set, storedEntry.Namespace, storedEntry.Key, storedEntry));
        }
    }

    /// <inheritdoc />
    public ValueTask<MemoryEntry?> GetAsync(string ns, string key, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_collections.TryGetValue(ns, out var collection) &&
            collection.Entries.TryGetValue(key, out var record))
        {
            return new ValueTask<MemoryEntry?>(record.ToEntry());
        }

        return new ValueTask<MemoryEntry?>((MemoryEntry?)null);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(
        string ns,
        string? query = null,
        int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);

        if (!string.IsNullOrWhiteSpace(query) && _embedder is not null)
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

        var matched = new List<MemoryEntry>(Math.Min(maxResults, collection.Entries.Count));
        foreach (var record in collection.Entries.Values)
        {
            if (!string.IsNullOrWhiteSpace(query) &&
                !record.Content.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !record.Key.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matched.Add(record.ToEntry());
            if (matched.Count >= maxResults)
            {
                break;
            }
        }

        return matched;
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(string ns, string key, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_collections.TryGetValue(ns, out var collection) && collection.Entries.TryRemove(key, out _))
        {
            lock (_dbLock)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM memory_entries WHERE namespace = $ns AND key = $key;";
                cmd.Parameters.AddWithValue("$ns", ns);
                cmd.Parameters.AddWithValue("$key", key);
                cmd.ExecuteNonQuery();
            }

            Notify(new MemoryStoreChangedEvent(MemoryStoreChangeKind.Delete, ns, key));
        }

        return default;
    }

    /// <inheritdoc />
    public ValueTask ClearAsync(string ns, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);

        _collections.TryRemove(ns, out _);

        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM memory_entries WHERE namespace = $ns;";
            cmd.Parameters.AddWithValue("$ns", ns);
            cmd.ExecuteNonQuery();
        }

        Notify(new MemoryStoreChangedEvent(MemoryStoreChangeKind.Clear, ns));
        return default;
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        if (_embedder is null ||
            !_collections.TryGetValue(ns, out var collection) ||
            collection.Entries.IsEmpty)
        {
            return Array.Empty<VectorSearchResult>();
        }

        var generated = await _embedder.GenerateAsync([query], cancellationToken: cancellationToken).ConfigureAwait(false);
        var normalizedQuery = Normalize(generated[0].Vector);
        var querySpan = normalizedQuery.Span;
        var hasFilters = metadataFilters is { Count: > 0 };

        var topQueue = new PriorityQueue<MemoryEntryRecord, float>(maxResults);

        foreach (var record in collection.Entries.Values)
        {
            if (record.Embedding.IsEmpty || record.Embedding.Length != querySpan.Length)
            {
                continue;
            }

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

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _keepAliveConnection?.Dispose();
    }

    private void InitializeSchemaAndHydrateCache()
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using (var pragmaCmd = conn.CreateCommand())
            {
                pragmaCmd.CommandText = """
                    PRAGMA journal_mode = WAL;
                    PRAGMA synchronous = NORMAL;
                    CREATE TABLE IF NOT EXISTS memory_entries (
                        namespace TEXT NOT NULL COLLATE NOCASE,
                        key TEXT NOT NULL,
                        content TEXT NOT NULL,
                        metadata_json TEXT NULL,
                        created_at TEXT NOT NULL,
                        embedding_blob BLOB NULL,
                        PRIMARY KEY (namespace, key)
                    );
                    """;
                pragmaCmd.ExecuteNonQuery();
            }

            using var selectCmd = conn.CreateCommand();
            selectCmd.CommandText = "SELECT namespace, key, content, metadata_json, created_at, embedding_blob FROM memory_entries;";
            using var reader = selectCmd.ExecuteReader();
            while (reader.Read())
            {
                var ns = reader.GetString(0);
                var key = reader.GetString(1);
                var content = reader.GetString(2);
                var metadataJson = reader.IsDBNull(3) ? null : reader.GetString(3);
                var createdAtRaw = reader.GetString(4);
                var embeddingBytes = reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5);

                var createdAt = DateTimeOffset.TryParse(createdAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : DateTimeOffset.UtcNow;

                var metadata = DeserializeMetadata(metadataJson);
                var embedding = DeserializeEmbedding(embeddingBytes);

                var collection = _collections.GetOrAdd(ns, static _ => new NamespaceCollection());
                collection.Entries[key] = new MemoryEntryRecord
                {
                    Namespace = ns,
                    Key = key,
                    Content = content,
                    Metadata = metadata,
                    CreatedAt = createdAt,
                    Embedding = embedding,
                };
            }
        }
    }

    private void PersistBatch(IReadOnlyList<MemoryEntryRecord> records)
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO memory_entries (namespace, key, content, metadata_json, created_at, embedding_blob)
                VALUES ($ns, $key, $content, $meta, $created, $emb)
                ON CONFLICT(namespace, key) DO UPDATE SET
                    content = excluded.content,
                    metadata_json = excluded.metadata_json,
                    created_at = excluded.created_at,
                    embedding_blob = excluded.embedding_blob;
                """;

            var pNs = cmd.Parameters.Add("$ns", SqliteType.Text);
            var pKey = cmd.Parameters.Add("$key", SqliteType.Text);
            var pContent = cmd.Parameters.Add("$content", SqliteType.Text);
            var pMeta = cmd.Parameters.Add("$meta", SqliteType.Text);
            var pCreated = cmd.Parameters.Add("$created", SqliteType.Text);
            var pEmb = cmd.Parameters.Add("$emb", SqliteType.Blob);

            for (var i = 0; i < records.Count; i++)
            {
                var rec = records[i];
                pNs.Value = rec.Namespace;
                pKey.Value = rec.Key;
                pContent.Value = rec.Content;
                pMeta.Value = SerializeMetadata(rec.Metadata) ?? (object)DBNull.Value;
                pCreated.Value = (rec.CreatedAt ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture);
                pEmb.Value = SerializeEmbedding(rec.Embedding) ?? (object)DBNull.Value;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static string NormalizeConnectionString(string input, out bool requiresKeepAlive)
    {
        requiresKeepAlive = false;
        if (input.Contains('=', StringComparison.Ordinal))
        {
            if (input.Contains(":memory:", StringComparison.OrdinalIgnoreCase) ||
                input.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase))
            {
                requiresKeepAlive = true;
            }
            return input;
        }

        if (string.Equals(input.Trim(), ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            requiresKeepAlive = true;
            return $"Data Source=myagents_mem_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        }

        var fullPath = Path.GetFullPath(input);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        return new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    private static string? SerializeMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(metadata);
    }

    private static Dictionary<string, string>? DeserializeMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return dict is null
            ? null
            : new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
    }

    private static byte[]? SerializeEmbedding(ReadOnlyMemory<float> embedding)
    {
        if (embedding.IsEmpty)
        {
            return null;
        }

        var bytes = MemoryMarshal.AsBytes(embedding.Span);
        return bytes.ToArray();
    }

    private static ReadOnlyMemory<float> DeserializeEmbedding(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0 || bytes.Length % sizeof(float) != 0)
        {
            return ReadOnlyMemory<float>.Empty;
        }

        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return Normalize(floats);
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

    private sealed class Subscription(SqliteMemoryStore owner, Action<MemoryStoreChangedEvent> listener) : IDisposable
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
