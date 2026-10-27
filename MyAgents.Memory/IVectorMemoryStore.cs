using MyAgents.Core.Memory;

namespace MyAgents.Memory;

/// <summary>
/// Extends <see cref="IMemoryStore"/> with semantic vector search.
/// Implementations use an embedding model to convert text to vectors and
/// find semantically similar entries using cosine similarity or similar metrics.
/// </summary>
public interface IVectorMemoryStore : IMemoryStore
{
    /// <summary>
    /// Searches for entries in the given namespace that are semantically similar
    /// to <paramref name="query"/> using vector similarity.
    /// </summary>
    /// <param name="ns">Logical namespace (e.g., "facts", "conversation").</param>
    /// <param name="query">Natural-language query to embed and search against.</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="minScore">
    /// Minimum similarity score threshold (0.0–1.0). Results below this are excluded.
    /// Defaults to 0.0 (return all results up to <paramref name="maxResults"/>).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Matching entries ordered by descending similarity score, each paired with its score.
    /// </returns>
    ValueTask<IReadOnlyList<VectorSearchResult>> VectorSearchAsync(
        string ns,
        string query,
        int maxResults = 5,
        float minScore = 0.0f,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A single result from a vector similarity search.
/// </summary>
/// <param name="Entry">The matched memory entry.</param>
/// <param name="Score">
/// Cosine similarity score in the range [0, 1].
/// Higher values indicate stronger semantic similarity.
/// </param>
public sealed record VectorSearchResult(MemoryEntry Entry, float Score);
