using System.Collections.Concurrent;
using MyAgents.Core.Memory;

namespace MyAgents.Memory;

/// <summary>
/// Hybrid memory retriever combining semantic dense embeddings with BM25 sparse lexical indexing.
/// Merges disparate ranking signals using Reciprocal Rank Fusion (RRF) for optimal recall and precision.
/// </summary>
public sealed class HybridMemoryRetriever : IHybridMemoryRetriever
{
    private readonly IMemoryStore _store;
    private readonly IVectorMemoryStore? _vectorStore;
    private readonly IMemoryTelemetrySink? _telemetrySink;
    private readonly ConcurrentDictionary<string, BM25Index> _bm25Indices = new(StringComparer.OrdinalIgnoreCase);

    public HybridMemoryRetriever(
        IMemoryStore store,
        IVectorMemoryStore? vectorStore = null,
        IMemoryTelemetrySink? telemetrySink = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vectorStore = vectorStore;
        _telemetrySink = telemetrySink;
    }

    /// <inheritdoc />
    public void IndexLexicalEntry(string ns, MemoryEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentNullException.ThrowIfNull(entry);

        var index = _bm25Indices.GetOrAdd(ns, _ => new BM25Index());
        index.AddOrUpdate(entry.Key, entry.Content);
    }

    /// <inheritdoc />
    public void RemoveLexicalEntry(string ns, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_bm25Indices.TryGetValue(ns, out var index))
        {
            index.Remove(key);
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HybridMemorySearchResult>> SearchHybridAsync(
        string ns,
        HybridMemoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentNullException.ThrowIfNull(query);

        await EnsureIndexPopulatedAsync(ns, cancellationToken).ConfigureAwait(false);

        var bm25Index = _bm25Indices.GetOrAdd(ns, _ => new BM25Index());
        var candidateFetchCount = Math.Max(query.MaxResults * 3, 20);

        // 1. Sparse BM25 Search
        var sparseHits = bm25Index.Search(query.QueryText, candidateFetchCount);
        var sparseRankMap = new Dictionary<string, (int Rank, double Score)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < sparseHits.Count; i++)
        {
            sparseRankMap[sparseHits[i].DocId] = (i + 1, sparseHits[i].Score);
        }

        // 2. Dense Vector Search
        var denseHits = _vectorStore is not null && !string.IsNullOrWhiteSpace(query.QueryText)
            ? await _vectorStore.VectorSearchAsync(ns, query.QueryText, candidateFetchCount, cancellationToken: cancellationToken).ConfigureAwait(false)
            : Array.Empty<VectorSearchResult>();

        var denseRankMap = new Dictionary<string, (int Rank, float Score, MemoryEntry Entry)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < denseHits.Count; i++)
        {
            var hit = denseHits[i];
            if (!denseRankMap.ContainsKey(hit.Entry.Key))
            {
                denseRankMap[hit.Entry.Key] = (denseRankMap.Count + 1, hit.Score, hit.Entry);
            }
        }

        // 3. Collect all candidate keys
        var allKeys = sparseRankMap.Keys.Concat(denseRankMap.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var results = new List<HybridMemorySearchResult>(allKeys.Length);

        var maxSparseScore = sparseHits.Count > 0 ? sparseHits.Max(h => h.Score) : 1.0;
        if (maxSparseScore <= 0.0) maxSparseScore = 1.0;

        foreach (var key in allKeys)
        {
            MemoryEntry? entry = null;
            if (denseRankMap.TryGetValue(key, out var denseData))
            {
                entry = denseData.Entry;
            }
            else
            {
                entry = await _store.GetAsync(ns, key, cancellationToken).ConfigureAwait(false);
            }

            if (entry is null || !MatchesMetadata(entry, query.MetadataFilters))
            {
                continue;
            }

            int? dRank = denseData.Rank != 0 ? denseData.Rank : null;
            float? dSim = denseData.Score != 0 ? denseData.Score : null;

            int? sRank = null;
            double? sScore = null;
            if (sparseRankMap.TryGetValue(key, out var sparseData))
            {
                sRank = sparseData.Rank;
                sScore = sparseData.Score;
            }

            double fusedScore;
            if (query.RankingMode == HybridRankingMode.ReciprocalRankFusion)
            {
                var denseRrf = dRank.HasValue ? query.DenseWeight / (query.RrfK + dRank.Value) : 0.0;
                var sparseRrf = sRank.HasValue ? query.SparseWeight / (query.RrfK + sRank.Value) : 0.0;
                fusedScore = denseRrf + sparseRrf;
            }
            else
            {
                var normSparse = (sScore ?? 0.0) / maxSparseScore;
                fusedScore = ((dSim ?? 0f) * query.DenseWeight) + (normSparse * query.SparseWeight);
            }

            if (fusedScore >= query.MinScore)
            {
                results.Add(new HybridMemorySearchResult(
                    Entry: entry,
                    FusedScore: fusedScore,
                    DenseRank: dRank,
                    DenseSimilarity: dSim,
                    SparseRank: sRank,
                    SparseScore: sScore));
            }
        }

        HybridMemorySearchResult[] sorted;
        if (results.Count <= query.MaxResults)
        {
            sorted = results
                .OrderByDescending(r => r.FusedScore)
                .ToArray();
        }
        else
        {
            var minHeap = new PriorityQueue<HybridMemorySearchResult, double>(query.MaxResults);
            foreach (var r in results)
            {
                if (minHeap.Count < query.MaxResults)
                {
                    minHeap.Enqueue(r, r.FusedScore);
                }
                else
                {
                    minHeap.TryPeek(out _, out var minScore);
                    if (r.FusedScore > minScore)
                    {
                        minHeap.DequeueEnqueue(r, r.FusedScore);
                    }
                }
            }

            sorted = new HybridMemorySearchResult[minHeap.Count];
            for (var i = sorted.Length - 1; i >= 0; i--)
            {
                minHeap.TryDequeue(out var item, out _);
                sorted[i] = item!;
            }
        }

        if (_telemetrySink is not null && sorted.Length > 0)
        {
            var top = sorted[0];
            await _telemetrySink.RecordAsync(new MemoryRetrievalTelemetry(
                Namespace: ns,
                QueryText: query.QueryText,
                CandidateCount: allKeys.Length,
                ResultCount: sorted.Length,
                TopResultKey: top.Entry.Key,
                TopResultScore: top.FusedScore,
                AverageScore: sorted.Average(r => r.FusedScore),
                RecordedAt: DateTimeOffset.UtcNow,
                ResultKeys: sorted.Select(r => r.Entry.Key).ToArray()), cancellationToken).ConfigureAwait(false);
        }

        return sorted;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
        string ns,
        MemoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentNullException.ThrowIfNull(query);

        var hybridQuery = new HybridMemoryQuery(
            QueryText: query.Text ?? string.Empty,
            MaxResults: query.MaxResults,
            DenseWeight: query.SemanticWeight,
            SparseWeight: query.KeywordWeight,
            MetadataFilters: query.MetadataFilters);

        var hybridResults = await SearchHybridAsync(ns, hybridQuery, cancellationToken).ConfigureAwait(false);

        var halfLife = query.RecencyHalfLife ?? TimeSpan.FromDays(7);
        return hybridResults.Select(hr =>
        {
            var recency = ScoreRecency(hr.Entry.CreatedAt, halfLife);
            var importance = ScoreImportance(hr.Entry.Metadata);
            return new MemorySearchResult(
                Entry: hr.Entry,
                Score: hr.FusedScore,
                SemanticScore: hr.DenseSimilarity ?? 0.0,
                KeywordScore: hr.SparseScore ?? 0.0,
                RecencyScore: recency,
                ImportanceScore: importance);
        }).ToArray();
    }

    private async ValueTask EnsureIndexPopulatedAsync(string ns, CancellationToken ct)
    {
        var index = _bm25Indices.GetOrAdd(ns, _ => new BM25Index());
        if (index.DocumentCount == 0)
        {
            // Seed index with current store contents
            var entries = await _store.SearchAsync(ns, maxResults: 500, cancellationToken: ct).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                index.AddOrUpdate(entry.Key, entry.Content);
            }
        }
    }

    private static bool MatchesMetadata(MemoryEntry entry, IReadOnlyDictionary<string, string>? filters)
    {
        if (filters is null || filters.Count == 0)
        {
            return true;
        }

        if (entry.Metadata is null)
        {
            return false;
        }

        foreach (var (key, value) in filters)
        {
            if (!entry.Metadata.TryGetValue(key, out var existing))
            {
                return false;
            }

            if (!string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static double ScoreRecency(DateTimeOffset? createdAt, TimeSpan halfLife)
    {
        if (createdAt is null) return 0.5;
        var age = DateTimeOffset.UtcNow - createdAt.Value;
        if (age <= TimeSpan.Zero) return 1.0;
        return Math.Pow(0.5, age.TotalSeconds / halfLife.TotalSeconds);
    }

    private static double ScoreImportance(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null) return 0.5;
        if (metadata.TryGetValue("importance", out var val) && !string.IsNullOrWhiteSpace(val))
        {
            if (double.TryParse(val, out var parsed)) return Math.Clamp(parsed, 0.0, 1.0);
        }
        return 0.5;
    }
}
