using System.Globalization;
using System.Text.RegularExpressions;
using MyAgents.Core.Memory;

namespace MyAgents.Memory;

/// <summary>
/// Represents a numbered citation marker (e.g., <c>[1]</c>) referenced in an assistant response
/// and resolved back to its retrieved <see cref="MemoryEntry"/>.
/// </summary>
/// <param name="CitationNumber">1-based citation index matching the <c>[n]</c> marker in the prompt context.</param>
/// <param name="Key">The underlying <see cref="MemoryEntry.Key"/>.</param>
/// <param name="Source">Resolved <see cref="DocumentMetadataKeys.Source"/> URI or identifier, if present.</param>
/// <param name="Title">Resolved <see cref="DocumentMetadataKeys.DocumentTitle"/>, if present.</param>
/// <param name="ChunkIndex">Resolved 0-based <see cref="DocumentMetadataKeys.ChunkIndex"/>, if present.</param>
/// <param name="ChunkCount">Resolved total <see cref="DocumentMetadataKeys.ChunkCount"/>, if present.</param>
/// <param name="Score">Relevance score of the retrieved entry.</param>
/// <param name="Entry">The underlying <see cref="MemoryEntry"/>.</param>
public sealed record GroundedCitation(
    int CitationNumber,
    string Key,
    string? Source,
    string? Title,
    int? ChunkIndex,
    int? ChunkCount,
    double Score,
    MemoryEntry Entry);

/// <summary>
/// Result of evaluating an assistant response's <c>[n]</c> citation markers against the
/// retrieved context entries injected during a turn.
/// </summary>
/// <param name="CitedSources">Resolved citations in order of first appearance in the response.</param>
/// <param name="InvalidCitationNumbers">Out-of-range <c>[n]</c> citation numbers that do not correspond to any provided context entry.</param>
/// <param name="ProvidedContextCount">Total number of numbered context entries (<c>[1]..[N]</c>) provided to the model.</param>
public sealed record CitationGroundingResult(
    IReadOnlyList<GroundedCitation> CitedSources,
    IReadOnlyList<int> InvalidCitationNumbers,
    int ProvidedContextCount)
{
    /// <summary>
    /// <see langword="true"/> when at least one valid citation was referenced and no out-of-range citations occurred.
    /// </summary>
    public bool IsGrounded => CitedSources.Count > 0 && InvalidCitationNumbers.Count == 0;

    /// <summary>
    /// <see langword="true"/> when the response contains one or more <c>[n]</c> markers outside <c>1..ProvidedContextCount</c>.
    /// </summary>
    public bool HasInvalidCitations => InvalidCitationNumbers.Count > 0;
}

/// <summary>
/// Extracts and verifies numbered <c>[n]</c> citations in model responses against retrieved memory results.
/// </summary>
public static partial class CitationGrounder
{
    [GeneratedRegex(@"\[(?<num>\d+)\]")]
    private static partial Regex CitationMarkerRegex();

    /// <summary>
    /// Evaluates <paramref name="responseText"/> against <paramref name="providedResults"/> (indexed 1..N)
    /// and returns resolved <see cref="GroundedCitation"/> records along with any out-of-bounds citation numbers.
    /// </summary>
    public static CitationGroundingResult Evaluate(
        string? responseText,
        IReadOnlyList<MemorySearchResult> providedResults)
    {
        ArgumentNullException.ThrowIfNull(providedResults);

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return new CitationGroundingResult([], [], providedResults.Count);
        }

        var cited = new List<GroundedCitation>();
        var invalid = new List<int>();
        var seenValid = new HashSet<int>();
        var seenInvalid = new HashSet<int>();

        var matches = CitationMarkerRegex().Matches(responseText);
        for (var i = 0; i < matches.Count; i++)
        {
            var group = matches[i].Groups["num"];
            if (!int.TryParse(group.ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var num))
            {
                continue;
            }

            if (num >= 1 && num <= providedResults.Count)
            {
                if (seenValid.Add(num))
                {
                    var result = providedResults[num - 1];
                    cited.Add(CreateGroundedCitation(num, result));
                }
            }
            else if (seenInvalid.Add(num))
            {
                invalid.Add(num);
            }
        }

        return new CitationGroundingResult(cited, invalid, providedResults.Count);
    }

    private static GroundedCitation CreateGroundedCitation(int citationNumber, MemorySearchResult result)
    {
        var metadata = result.Entry.Metadata;
        string? source = null;
        string? title = null;
        int? chunkIndex = null;
        int? chunkCount = null;

        if (metadata is not null)
        {
            if (metadata.TryGetValue(DocumentMetadataKeys.Source, out var rawSource) &&
                !string.IsNullOrWhiteSpace(rawSource))
            {
                source = rawSource;
            }

            if (metadata.TryGetValue(DocumentMetadataKeys.DocumentTitle, out var rawTitle) &&
                !string.IsNullOrWhiteSpace(rawTitle))
            {
                title = rawTitle;
            }

            if (metadata.TryGetValue(DocumentMetadataKeys.ChunkIndex, out var rawIdx) &&
                int.TryParse(rawIdx, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedIdx))
            {
                chunkIndex = parsedIdx;
            }

            if (metadata.TryGetValue(DocumentMetadataKeys.ChunkCount, out var rawCount) &&
                int.TryParse(rawCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCount))
            {
                chunkCount = parsedCount;
            }
        }

        return new GroundedCitation(
            CitationNumber: citationNumber,
            Key: result.Entry.Key,
            Source: source,
            Title: title,
            ChunkIndex: chunkIndex,
            ChunkCount: chunkCount,
            Score: result.Score,
            Entry: result.Entry);
    }
}
