using MyAgents.Core;

namespace MyAgents.Memory;

/// <summary>
/// Default <see cref="ITextChunker"/> implementation that splits text along a hierarchy of
/// paragraph, line, sentence, clause, and word separators before falling back to hard character limits.
/// Supports both character budgets (<see cref="TextChunkingOptions.ChunkSize"/>) and token budgets
/// (<see cref="TextChunkingOptions.MaxTokens"/>) via <see cref="ITokenCounter"/>.
/// </summary>
public sealed class RecursiveCharacterTextChunker : ITextChunker
{
    private static readonly string[] s_defaultSeparators =
    [
        "\n\n",
        "\r\n\r\n",
        "\n",
        "\r\n",
        ". ",
        "! ",
        "? ",
        "; ",
        ", ",
        " ",
    ];

    private readonly TextChunkingOptions _defaultOptions;
    private readonly ITokenCounter _tokenCounter;

    /// <summary>
    /// Creates a new <see cref="RecursiveCharacterTextChunker"/> with optional default chunking options and token counter.
    /// </summary>
    public RecursiveCharacterTextChunker(
        TextChunkingOptions? defaultOptions = null,
        ITokenCounter? tokenCounter = null)
    {
        _defaultOptions = defaultOptions ?? new TextChunkingOptions();
        ValidateOptions(_defaultOptions);
        _tokenCounter = tokenCounter ?? ApproximateTokenCounter.Instance;
    }

    /// <inheritdoc />
    public IReadOnlyList<TextChunk> Chunk(string text, TextChunkingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var effectiveOptions = options ?? _defaultOptions;
        ValidateOptions(effectiveOptions);

        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var chunkSize = effectiveOptions.ChunkSize;
        var chunkOverlap = effectiveOptions.ChunkOverlap;
        var maxTokens = effectiveOptions.MaxTokens;

        if (text.Length <= chunkSize &&
            (maxTokens is null || CountSpanTokens(text.AsSpan()) <= maxTokens.Value))
        {
            return [new TextChunk(0, text, 0, text.Length)];
        }

        var separators = effectiveOptions.Separators is { Count: > 0 } customSeparators
            ? customSeparators
            : s_defaultSeparators;

        var chunks = new List<TextChunk>();
        var start = 0;
        var previousEnd = 0;

        while (start < text.Length)
        {
            var maxEnd = Math.Min(text.Length, start + chunkSize);
            if (maxTokens is { } tokenBudget)
            {
                maxEnd = ConstrainMaxEndByTokenBudget(text, start, maxEnd, tokenBudget);
            }

            if (maxEnd >= text.Length)
            {
                var finalLength = text.Length - start;
                chunks.Add(new TextChunk(
                    Index: chunks.Count,
                    Content: text.Substring(start, finalLength),
                    StartOffset: start,
                    Length: finalLength));
                break;
            }

            var minSplit = Math.Max(start + 1, previousEnd + 1);
            if (minSplit > maxEnd)
            {
                maxEnd = minSplit;
            }

            var splitEnd = FindSplitEnd(text, minSplit, maxEnd, separators);

            var length = splitEnd - start;
            chunks.Add(new TextChunk(
                Index: chunks.Count,
                Content: text.Substring(start, length),
                StartOffset: start,
                Length: length));

            previousEnd = splitEnd;
            var effectiveOverlap = Math.Min(chunkOverlap, Math.Max(0, length - 1));
            start = splitEnd - effectiveOverlap;
        }

        return chunks;
    }

    private int ConstrainMaxEndByTokenBudget(
        string text,
        int start,
        int maxEnd,
        int maxTokens)
    {
        var span = text.AsSpan(start, maxEnd - start);
        if (_tokenCounter is ApproximateTokenCounter)
        {
            var length = ApproximateTokenCounter.FindMaxLengthForTokenBudget(span, maxTokens);
            return start + Math.Max(1, length);
        }

        if (CountSpanTokens(span) <= maxTokens)
        {
            return maxEnd;
        }

        var low = start + 1;
        var high = maxEnd;
        var best = low;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var tokens = CountSpanTokens(text.AsSpan(start, mid - start));
            if (tokens <= maxTokens)
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best;
    }

    private int CountSpanTokens(ReadOnlySpan<char> span)
        => _tokenCounter is ApproximateTokenCounter
            ? ApproximateTokenCounter.CountTokens(span)
            : _tokenCounter.CountTokens(span.ToString());

    private static int FindSplitEnd(
        string text,
        int minSplit,
        int maxEnd,
        IReadOnlyList<string> separators)
    {
        for (var i = 0; i < separators.Count; i++)
        {
            var sep = separators[i];
            if (string.IsNullOrEmpty(sep))
            {
                continue;
            }

            var searchStart = Math.Max(0, minSplit - sep.Length);
            var searchLength = maxEnd - searchStart;
            if (searchLength < sep.Length)
            {
                continue;
            }

            var windowSpan = text.AsSpan(searchStart, searchLength);
            var lastIdx = windowSpan.LastIndexOf(sep.AsSpan(), StringComparison.Ordinal);
            if (lastIdx >= 0)
            {
                var candidateEnd = searchStart + lastIdx + sep.Length;
                if (candidateEnd >= minSplit && candidateEnd <= maxEnd)
                {
                    return candidateEnd;
                }
            }
        }

        return maxEnd;
    }

    private static void ValidateOptions(TextChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ChunkSize, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(options.ChunkOverlap);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(options.ChunkOverlap, options.ChunkSize);
        if (options.MaxTokens is { } maxTokens)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxTokens, 0);
        }
    }
}
