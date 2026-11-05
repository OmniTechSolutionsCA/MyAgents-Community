using MyAgents.Core.Memory;

namespace MyAgents.Memory;

/// <summary>
/// Well-known metadata keys attached to ingested document chunks for provenance and citation rendering.
/// </summary>
public static class DocumentMetadataKeys
{
    /// <summary>Source URI or identifier of the document.</summary>
    public const string Source = "source";

    /// <summary>Human-readable title of the document.</summary>
    public const string DocumentTitle = "document_title";

    /// <summary>Zero-based index of the chunk within the source document.</summary>
    public const string ChunkIndex = "chunk_index";

    /// <summary>Total number of chunks produced for the source document.</summary>
    public const string ChunkCount = "chunk_count";
}

/// <summary>
/// Represents a source document to be chunked, embedded, and upserted into a memory namespace.
/// </summary>
/// <param name="Source">Unique source URI or document identifier within the target namespace.</param>
/// <param name="Content">Full raw text content of the document.</param>
/// <param name="Title">Optional human-readable document title; defaults to <paramref name="Source"/> when omitted.</param>
/// <param name="Metadata">Optional additional metadata copied to every chunk entry.</param>
/// <param name="ChunkingOptions">Optional per-document chunking options overriding the ingestor's default chunker configuration.</param>
public sealed record IngestDocument(
    string Source,
    string Content,
    string? Title = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    TextChunkingOptions? ChunkingOptions = null);

/// <summary>
/// Summary of a completed document ingestion operation.
/// </summary>
/// <param name="Namespace">Target memory namespace.</param>
/// <param name="Source">Source URI or identifier of the ingested document.</param>
/// <param name="Chunks">The stored chunk <see cref="MemoryEntry"/> records in chunk order.</param>
/// <param name="ReplacedChunkCount">Number of chunks from a previous version of the same source that were replaced or removed.</param>
public sealed record DocumentIngestionResult(
    string Namespace,
    string Source,
    IReadOnlyList<MemoryEntry> Chunks,
    int ReplacedChunkCount);

/// <summary>
/// Summary of a completed multi-document batch ingestion operation.
/// </summary>
/// <param name="Namespace">Target memory namespace.</param>
/// <param name="Documents">Per-document ingestion results in input order.</param>
/// <param name="TotalChunks">Total number of chunks stored across all documents.</param>
/// <param name="TotalReplacedChunks">Total number of prior chunks replaced or removed across all documents.</param>
public sealed record BatchDocumentIngestionResult(
    string Namespace,
    IReadOnlyList<DocumentIngestionResult> Documents,
    int TotalChunks,
    int TotalReplacedChunks);

/// <summary>
/// Orchestrates text chunking, single-batch embedding generation, provenance metadata tagging,
/// store persistence, and lexical index synchronization for RAG corpora.
/// </summary>
public interface IDocumentIngestor
{
    /// <summary>
    /// Chunks, embeds, and stores <paramref name="document"/> into <paramref name="ns"/>, replacing any
    /// previously ingested chunks for the same <see cref="IngestDocument.Source"/>.
    /// </summary>
    ValueTask<DocumentIngestionResult> IngestAsync(
        string ns,
        IngestDocument document,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Chunks, embeds in a single batch call, and stores all <paramref name="documents"/> into <paramref name="ns"/>.
    /// </summary>
    ValueTask<BatchDocumentIngestionResult> IngestBatchAsync(
        string ns,
        IReadOnlyList<IngestDocument> documents,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all chunks belonging to <paramref name="source"/> from <paramref name="ns"/> and removes them from the lexical index.
    /// </summary>
    /// <returns>The number of chunk entries removed.</returns>
    ValueTask<int> DeleteDocumentAsync(
        string ns,
        string source,
        CancellationToken cancellationToken = default);
}
