namespace MyAgents.Memory;

/// <summary>
/// Options controlling character-budget and optional token-budget text splitting and chunk overlap.
/// </summary>
/// <param name="ChunkSize">Maximum number of characters per chunk. Must be greater than zero.</param>
/// <param name="ChunkOverlap">Number of overlapping characters shared between consecutive chunks. Must be non-negative and less than <paramref name="ChunkSize"/>.</param>
/// <param name="Separators">Ordered list of preferred boundary separators from highest to lowest priority.</param>
/// <param name="MaxTokens">Optional maximum token budget per chunk evaluated via <see cref="ITokenCounter"/>.</param>
public sealed record TextChunkingOptions(
    int ChunkSize = 1000,
    int ChunkOverlap = 100,
    IReadOnlyList<string>? Separators = null,
    int? MaxTokens = null);

/// <summary>
/// A contiguous slice of a source document produced by an <see cref="ITextChunker"/>.
/// </summary>
/// <param name="Index">Zero-based index of the chunk within the document.</param>
/// <param name="Content">The chunk text slice.</param>
/// <param name="StartOffset">Zero-based character offset in the source text where this chunk begins.</param>
/// <param name="Length">Length of the chunk in characters.</param>
public sealed record TextChunk(
    int Index,
    string Content,
    int StartOffset,
    int Length);

/// <summary>
/// Splits document text into size-bounded, optionally overlapping chunks.
/// </summary>
public interface ITextChunker
{
    /// <summary>
    /// Splits <paramref name="text"/> into ordered chunks according to <paramref name="options"/>.
    /// </summary>
    IReadOnlyList<TextChunk> Chunk(string text, TextChunkingOptions? options = null);
}
