using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace MyAgents.Memory;

/// <summary>
/// Thread-safe, bounded content-hash caching decorator for <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>.
/// Eliminates redundant embedding generation calls across identical queries and unchanged document chunks.
/// </summary>
public sealed class CachingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _inner;
    private readonly int _maxCacheEntries;
    private readonly ConcurrentDictionary<string, Embedding<float>> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _insertionOrder = new();
    private int _cacheCount;
    private long _cacheHits;
    private long _cacheMisses;

    /// <summary>
    /// Creates a new <see cref="CachingEmbeddingGenerator"/> wrapping <paramref name="inner"/>.
    /// </summary>
    /// <param name="inner">The underlying embedding generator.</param>
    /// <param name="maxCacheEntries">Maximum number of unique text embeddings to retain in memory (default: 4096).</param>
    public CachingEmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> inner,
        int maxCacheEntries = 4096)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxCacheEntries, 0);
        _inner = inner;
        _maxCacheEntries = maxCacheEntries;
    }

    /// <summary>
    /// Total number of embedding requests served directly from the content-hash cache.
    /// </summary>
    public long CacheHits => Interlocked.Read(ref _cacheHits);

    /// <summary>
    /// Total number of embedding requests forwarded to the inner <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>.
    /// </summary>
    public long CacheMisses => Interlocked.Read(ref _cacheMisses);

    /// <summary>
    /// Current number of cached embeddings.
    /// </summary>
    public int CachedCount => _cache.Count;

    /// <inheritdoc />
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var items = values as IReadOnlyList<string> ?? values.ToList();
        if (items.Count == 0)
        {
            return new GeneratedEmbeddings<Embedding<float>>();
        }

        var resolved = new Embedding<float>?[items.Count];
        var hashes = new string[items.Count];
        List<string>? missingTexts = null;
        List<int>? missingIndices = null;
        Dictionary<string, List<int>>? duplicateMissingByHash = null;

        for (var i = 0; i < items.Count; i++)
        {
            var text = items[i] ?? string.Empty;
            var hash = ComputeContentHash(text);
            hashes[i] = hash;

            if (_cache.TryGetValue(hash, out var cached))
            {
                resolved[i] = cached;
                Interlocked.Increment(ref _cacheHits);
            }
            else
            {
                Interlocked.Increment(ref _cacheMisses);
                duplicateMissingByHash ??= new Dictionary<string, List<int>>(StringComparer.Ordinal);
                if (duplicateMissingByHash.TryGetValue(hash, out var indices))
                {
                    indices.Add(i);
                }
                else
                {
                    duplicateMissingByHash[hash] = [i];
                    missingTexts ??= new List<string>(items.Count - i);
                    missingIndices ??= new List<int>(items.Count - i);
                    missingTexts.Add(text);
                    missingIndices.Add(i);
                }
            }
        }

        if (missingTexts is { Count: > 0 } && missingIndices is not null && duplicateMissingByHash is not null)
        {
            var generated = await _inner.GenerateAsync(missingTexts, options, cancellationToken).ConfigureAwait(false);
            if (generated.Count != missingTexts.Count)
            {
                throw new InvalidOperationException(
                    $"Inner embedding generator returned {generated.Count} embeddings for {missingTexts.Count} uncached inputs.");
            }

            for (var m = 0; m < missingTexts.Count; m++)
            {
                var embedding = generated[m];
                var primaryIdx = missingIndices[m];
                var hash = hashes[primaryIdx];

                StoreInCache(hash, embedding);

                var targetIndices = duplicateMissingByHash[hash];
                for (var t = 0; t < targetIndices.Count; t++)
                {
                    resolved[targetIndices[t]] = embedding;
                }
            }
        }

        var result = new GeneratedEmbeddings<Embedding<float>>(items.Count);
        for (var i = 0; i < resolved.Length; i++)
        {
            result.Add(resolved[i]!);
        }

        return result;
    }

    /// <summary>
    /// Clears all cached embeddings and resets hit/miss counters.
    /// </summary>
    public void Clear()
    {
        _cache.Clear();
        while (_insertionOrder.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref _cacheCount, 0);
        Interlocked.Exchange(ref _cacheHits, 0);
        Interlocked.Exchange(ref _cacheMisses, 0);
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this)
            ? this
            : _inner.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();

    private void StoreInCache(string hash, Embedding<float> embedding)
    {
        if (!_cache.TryAdd(hash, embedding))
        {
            return;
        }

        _insertionOrder.Enqueue(hash);
        var current = Interlocked.Increment(ref _cacheCount);
        while (current > _maxCacheEntries && _insertionOrder.TryDequeue(out var oldest))
        {
            if (_cache.TryRemove(oldest, out _))
            {
                current = Interlocked.Decrement(ref _cacheCount);
            }
        }
    }

    private static string ComputeContentHash(string text)
    {
        var byteCount = Encoding.UTF8.GetByteCount(text);
        byte[]? rented = null;
        Span<byte> utf8Bytes = byteCount <= 512
            ? stackalloc byte[byteCount]
            : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);

        try
        {
            Encoding.UTF8.GetBytes(text.AsSpan(), utf8Bytes);
            Span<byte> hashBytes = stackalloc byte[32];
            SHA256.HashData(utf8Bytes, hashBytes);
            return Convert.ToHexString(hashBytes);
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
