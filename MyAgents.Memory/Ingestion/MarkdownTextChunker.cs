using System.Text;
using MyAgents.Core;

namespace MyAgents.Memory;

/// <summary>
/// Structure-aware Markdown <see cref="ITextChunker"/> that splits documents along ATX heading
/// boundaries (<c>#</c>–<c>######</c>), keeps fenced code blocks (<c>```</c> / <c>~~~</c>) intact
/// whenever they fit within the configured budget, and optionally prepends active parent heading
/// breadcrumbs to chunks so subsections retain their document hierarchy context.
/// </summary>
public sealed class MarkdownTextChunker : ITextChunker
{
    private readonly TextChunkingOptions _defaultOptions;
    private readonly ITokenCounter _tokenCounter;
    private readonly bool _includeHeadingBreadcrumbs;

    /// <summary>
    /// Creates a new <see cref="MarkdownTextChunker"/>.
    /// </summary>
    /// <param name="defaultOptions">Default chunking options when none are passed to <see cref="Chunk"/>.</param>
    /// <param name="tokenCounter">Token counter used when <see cref="TextChunkingOptions.MaxTokens"/> is set.</param>
    /// <param name="includeHeadingBreadcrumbs">
    /// When <see langword="true"/>, prepends ancestor heading context (e.g., <c># Guide &gt; ## Setup</c>)
    /// to chunks that do not already begin with the full heading path.
    /// </param>
    public MarkdownTextChunker(
        TextChunkingOptions? defaultOptions = null,
        ITokenCounter? tokenCounter = null,
        bool includeHeadingBreadcrumbs = true)
    {
        _defaultOptions = defaultOptions ?? new TextChunkingOptions();
        ValidateOptions(_defaultOptions);
        _tokenCounter = tokenCounter ?? ApproximateTokenCounter.Instance;
        _includeHeadingBreadcrumbs = includeHeadingBreadcrumbs;
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

        var blocks = ParseMarkdownBlocks(text);
        if (blocks.Count == 0)
        {
            return [];
        }

        var fallbackChunker = new RecursiveCharacterTextChunker(effectiveOptions, _tokenCounter);
        var chunks = new List<TextChunk>();

        var currentBuilder = new StringBuilder();
        var currentStartOffset = -1;
        var currentEndOffset = 0;
        string? currentBreadcrumb = null;

        void FlushCurrent()
        {
            if (currentBuilder.Length == 0 || currentStartOffset < 0)
            {
                return;
            }

            var content = currentBuilder.ToString();
            chunks.Add(new TextChunk(
                Index: chunks.Count,
                Content: content,
                StartOffset: currentStartOffset,
                Length: currentEndOffset - currentStartOffset));

            currentBuilder.Clear();
            currentStartOffset = -1;
        }

        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var blockSpan = text.AsSpan(block.StartOffset, block.Length);

            // Heading boundary starts a new logical section when we already accumulated body content
            if (block.IsHeading && currentBuilder.Length > 0)
            {
                FlushCurrent();
            }

            var prefix = GetBreadcrumbPrefix(block.Breadcrumb, blockSpan);
            var candidateIfAppended = currentBuilder.Length == 0
                ? string.Concat(prefix, blockSpan)
                : string.Concat(currentBuilder.ToString(), blockSpan);

            if (FitsBudget(candidateIfAppended, effectiveOptions))
            {
                if (currentBuilder.Length == 0)
                {
                    currentStartOffset = block.StartOffset;
                    currentBreadcrumb = block.Breadcrumb;
                    if (prefix.Length > 0)
                    {
                        currentBuilder.Append(prefix);
                    }
                }

                currentBuilder.Append(blockSpan);
                currentEndOffset = block.StartOffset + block.Length;
                continue;
            }

            // Current accumulator cannot fit this block -> flush accumulator first
            FlushCurrent();

            var standaloneWithPrefix = string.Concat(prefix, blockSpan);
            if (FitsBudget(standaloneWithPrefix, effectiveOptions))
            {
                currentStartOffset = block.StartOffset;
                currentEndOffset = block.StartOffset + block.Length;
                currentBreadcrumb = block.Breadcrumb;
                currentBuilder.Append(standaloneWithPrefix);
                continue;
            }

            // Block itself exceeds budget: split via RecursiveCharacterTextChunker
            var rawBlockText = blockSpan.ToString();
            var subOptions = effectiveOptions;
            if (prefix.Length > 0 && prefix.Length < effectiveOptions.ChunkSize / 2)
            {
                var adjustedSize = Math.Max(1, effectiveOptions.ChunkSize - prefix.Length);
                var adjustedOverlap = Math.Min(effectiveOptions.ChunkOverlap, Math.Max(0, adjustedSize - 1));
                int? adjustedTokens = effectiveOptions.MaxTokens is { } maxTok
                    ? Math.Max(1, maxTok - CountTokens(prefix))
                    : null;
                subOptions = effectiveOptions with
                {
                    ChunkSize = adjustedSize,
                    ChunkOverlap = adjustedOverlap,
                    MaxTokens = adjustedTokens,
                };
            }

            var subChunks = fallbackChunker.Chunk(rawBlockText, subOptions);
            for (var s = 0; s < subChunks.Count; s++)
            {
                var sub = subChunks[s];
                var subPrefix = GetBreadcrumbPrefix(block.Breadcrumb, sub.Content.AsSpan());
                var subContent = subPrefix.Length > 0 && FitsBudget(subPrefix + sub.Content, effectiveOptions)
                    ? subPrefix + sub.Content
                    : sub.Content;

                chunks.Add(new TextChunk(
                    Index: chunks.Count,
                    Content: subContent,
                    StartOffset: block.StartOffset + sub.StartOffset,
                    Length: sub.Length));
            }

            _ = currentBreadcrumb;
        }

        FlushCurrent();
        return chunks;
    }

    private string GetBreadcrumbPrefix(string? breadcrumb, ReadOnlySpan<char> contentSpan)
    {
        if (!_includeHeadingBreadcrumbs || string.IsNullOrWhiteSpace(breadcrumb))
        {
            return string.Empty;
        }

        var trimmed = contentSpan.TrimStart();
        if (trimmed.StartsWith(breadcrumb.AsSpan(), StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return $"[{breadcrumb}]\n";
    }

    private bool FitsBudget(string candidate, TextChunkingOptions options)
    {
        if (candidate.Length > options.ChunkSize)
        {
            return false;
        }

        if (options.MaxTokens is { } maxTokens && CountTokens(candidate) > maxTokens)
        {
            return false;
        }

        return true;
    }

    private int CountTokens(string text)
        => _tokenCounter is ApproximateTokenCounter
            ? ApproximateTokenCounter.CountTokens(text.AsSpan())
            : _tokenCounter.CountTokens(text);

    private static List<MarkdownBlock> ParseMarkdownBlocks(string text)
    {
        var blocks = new List<MarkdownBlock>();
        var headings = new string?[7]; // 1..6

        var pos = 0;
        var blockStart = 0;
        var inCodeFence = false;
        var fenceChar = '\0';
        var fenceLen = 0;

        while (pos < text.Length)
        {
            var lineStart = pos;
            var lineEnd = text.IndexOf('\n', pos);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }
            else
            {
                lineEnd++; // include '\n'
            }

            var lineSpan = text.AsSpan(lineStart, lineEnd - lineStart).TrimEnd("\r\n".AsSpan());

            if (TryMatchFence(lineSpan, out var matchedChar, out var matchedLen))
            {
                if (!inCodeFence)
                {
                    if (lineStart > blockStart)
                    {
                        blocks.Add(new MarkdownBlock(blockStart, lineStart - blockStart, BuildBreadcrumb(headings), IsHeading: false));
                    }

                    blockStart = lineStart;
                    inCodeFence = true;
                    fenceChar = matchedChar;
                    fenceLen = matchedLen;
                }
                else if (matchedChar == fenceChar && matchedLen >= fenceLen)
                {
                    inCodeFence = false;
                    blocks.Add(new MarkdownBlock(blockStart, lineEnd - blockStart, BuildBreadcrumb(headings), IsHeading: false));
                    blockStart = lineEnd;
                }

                pos = lineEnd;
                continue;
            }

            if (!inCodeFence)
            {
                if (TryParseAtxHeading(lineSpan, out var level, out var headingTitle))
                {
                    if (lineStart > blockStart)
                    {
                        blocks.Add(new MarkdownBlock(blockStart, lineStart - blockStart, BuildBreadcrumb(headings), IsHeading: false));
                    }

                    headings[level] = headingTitle;
                    for (var l = level + 1; l <= 6; l++)
                    {
                        headings[l] = null;
                    }

                    var breadcrumb = BuildBreadcrumb(headings);
                    blocks.Add(new MarkdownBlock(lineStart, lineEnd - lineStart, breadcrumb, IsHeading: true));
                    blockStart = lineEnd;
                    pos = lineEnd;
                    continue;
                }

                if (lineSpan.IsWhiteSpace() && lineStart > blockStart)
                {
                    blocks.Add(new MarkdownBlock(blockStart, lineEnd - blockStart, BuildBreadcrumb(headings), IsHeading: false));
                    blockStart = lineEnd;
                }
            }

            pos = lineEnd;
        }

        if (blockStart < text.Length)
        {
            blocks.Add(new MarkdownBlock(blockStart, text.Length - blockStart, BuildBreadcrumb(headings), IsHeading: false));
        }

        return blocks;
    }

    private static bool TryMatchFence(ReadOnlySpan<char> line, out char fenceChar, out int fenceLen)
    {
        var trimmed = line.TrimStart(' ');
        if (trimmed.Length >= 3 && (trimmed[0] == '`' || trimmed[0] == '~'))
        {
            var ch = trimmed[0];
            var count = 0;
            while (count < trimmed.Length && trimmed[count] == ch)
            {
                count++;
            }

            if (count >= 3)
            {
                fenceChar = ch;
                fenceLen = count;
                return true;
            }
        }

        fenceChar = '\0';
        fenceLen = 0;
        return false;
    }

    private static bool TryParseAtxHeading(ReadOnlySpan<char> line, out int level, out string title)
    {
        var trimmed = line.TrimStart(' ');
        var hashes = 0;
        while (hashes < trimmed.Length && hashes < 7 && trimmed[hashes] == '#')
        {
            hashes++;
        }

        if (hashes is >= 1 and <= 6 && trimmed.Length > hashes && trimmed[hashes] == ' ')
        {
            var headingText = trimmed[(hashes + 1)..].Trim().TrimEnd('#').Trim();
            if (!headingText.IsEmpty)
            {
                level = hashes;
                title = $"{new string('#', hashes)} {headingText}";
                return true;
            }
        }

        level = 0;
        title = string.Empty;
        return false;
    }

    private static string? BuildBreadcrumb(string?[] headings)
    {
        StringBuilder? sb = null;
        for (var i = 1; i <= 6; i++)
        {
            if (!string.IsNullOrEmpty(headings[i]))
            {
                sb ??= new StringBuilder();
                if (sb.Length > 0)
                {
                    sb.Append(" > ");
                }

                sb.Append(headings[i]);
            }
        }

        return sb?.ToString();
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

    private readonly record struct MarkdownBlock(
        int StartOffset,
        int Length,
        string? Breadcrumb,
        bool IsHeading);
}
