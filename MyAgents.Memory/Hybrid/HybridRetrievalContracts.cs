using MyAgents.Core.Memory;

namespace MyAgents.Memory;

/// <summary>
/// Method used to merge dense vector similarity and sparse BM25 lexical signals.
/// </summary>
public enum HybridRankingMode
{
    /// <summary>
    /// Reciprocal Rank Fusion (RRF) using rank reciprocals: score = sum(weight / (k + rank)).
    /// Standardized by modern search engines to eliminate scale disparity between dense and sparse metrics.
    /// </summary>
    ReciprocalRankFusion,

    /// <summary>
    /// Normalized linear score interpolation: score = (denseWeight * denseScore) + (sparseWeight * sparseScore).
    /// </summary>
    WeightedLinearCombination,
}

/// <summary>
/// Query parameters for hybrid semantic + BM25 sparse memory retrieval.
/// </summary>
public sealed record HybridMemoryQuery(
    string QueryText,
    int MaxResults = 5,
    double DenseWeight = 0.5,
    double SparseWeight = 0.5,
    int RrfK = 60,
    HybridRankingMode RankingMode = HybridRankingMode.ReciprocalRankFusion,
    double MinScore = 0.0,
    IReadOnlyDictionary<string, string>? MetadataFilters = null);

/// <summary>
/// Hybrid search result containing both dense vector and sparse BM25 rank/score metrics.
/// </summary>
public sealed record HybridMemorySearchResult(
    MemoryEntry Entry,
    double FusedScore,
    int? DenseRank = null,
    float? DenseSimilarity = null,
    int? SparseRank = null,
    double? SparseScore = null);

/// <summary>
/// Specialized contract for hybrid dense + BM25 memory retrieval.
/// </summary>
public interface IHybridMemoryRetriever : IMemoryRetriever
{
    /// <summary>
    /// Executes hybrid retrieval combining vector similarity and BM25 sparse index over a given namespace.
    /// </summary>
    ValueTask<IReadOnlyList<HybridMemorySearchResult>> SearchHybridAsync(
        string ns,
        HybridMemoryQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronizes or indexes an entry into the BM25 sparse engine.
    /// </summary>
    void IndexLexicalEntry(string ns, MemoryEntry entry);

    /// <summary>
    /// Removes an entry from the BM25 sparse index.
    /// </summary>
    void RemoveLexicalEntry(string ns, string key);
}