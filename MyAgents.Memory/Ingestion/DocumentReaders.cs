using System.Net;
using System.Text;

namespace MyAgents.Memory;

/// <summary>
/// Abstraction for extracting normalized text, title, and metadata from streams or files
/// (such as HTML, Markdown, JSON, source code, or custom binary document formats like PDF/DOCX)
/// into an <see cref="IngestDocument"/> ready for <see cref="IDocumentIngestor"/>.
/// </summary>
public interface IDocumentReader
{
    /// <summary>
    /// Returns <see langword="true"/> if this reader can parse the given media type or file extension
    /// (for example <c>"text/html"</c>, <c>".html"</c>, <c>".md"</c>, or <c>"application/pdf"</c>).
    /// </summary>
    bool CanRead(string contentTypeOrExtension);

    /// <summary>
    /// Reads and extracts an <see cref="IngestDocument"/> from <paramref name="content"/>.
    /// </summary>
    /// <param name="content">The source document stream.</param>
    /// <param name="source">Canonical URI or identifier of the document.</param>
    /// <param name="contentTypeOrExtension">Optional media type or file extension hint (e.g., <c>"text/html"</c> or <c>".md"</c>).</param>
    /// <param name="metadata">Optional caller-supplied metadata to attach to the extracted document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<IngestDocument> ReadAsync(
        Stream content,
        string source,
        string? contentTypeOrExtension = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Zero-dependency <see cref="IDocumentReader"/> that extracts structured text and titles from HTML documents.
/// Strips <c>&lt;script&gt;</c>, <c>&lt;style&gt;</c>, <c>&lt;svg&gt;</c>, and <c>&lt;noscript&gt;</c> blocks,
/// converts HTML headings (<c>&lt;h1&gt;</c>–<c>&lt;h6&gt;</c>), paragraphs, list items, and line breaks into
/// Markdown-compatible text, and decodes HTML entities.
/// </summary>
public sealed class HtmlDocumentReader : IDocumentReader
{
    /// <summary>
    /// Shared singleton instance.
    /// </summary>
    public static HtmlDocumentReader Instance { get; } = new();

    /// <inheritdoc />
    public bool CanRead(string contentTypeOrExtension)
    {
        if (string.IsNullOrWhiteSpace(contentTypeOrExtension))
        {
            return false;
        }

        var normalized = NormalizeMediaOrExtension(contentTypeOrExtension);
        return normalized is ".html" or ".htm" or ".xhtml" or "text/html" or "application/xhtml+xml";
    }

    /// <inheritdoc />
    public async ValueTask<IngestDocument> ReadAsync(
        Stream content,
        string source,
        string? contentTypeOrExtension = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        var html = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return ParseHtml(html, source, metadata);
    }

    /// <summary>
    /// Parses raw HTML text into an <see cref="IngestDocument"/> with extracted <c>&lt;title&gt;</c> and structured Markdown/plain text.
    /// </summary>
    public static IngestDocument ParseHtml(
        string html,
        string source,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var title = ExtractTagInnerText(html.AsSpan(), "title");
        var extractedText = ConvertHtmlToStructuredText(html.AsSpan());

        return new IngestDocument(
            Source: source,
            Content: extractedText,
            Title: string.IsNullOrWhiteSpace(title) ? null : title,
            Metadata: metadata);
    }

    private static string? ExtractTagInnerText(ReadOnlySpan<char> html, string tagName)
    {
        var openMarker = $"<{tagName}";
        var closeMarker = $"</{tagName}>";

        var openIdx = html.IndexOf(openMarker.AsSpan(), StringComparison.OrdinalIgnoreCase);
        if (openIdx < 0)
        {
            return null;
        }

        var afterOpen = html[(openIdx + openMarker.Length)..];
        var tagEnd = afterOpen.IndexOf('>');
        if (tagEnd < 0)
        {
            return null;
        }

        var contentSpan = afterOpen[(tagEnd + 1)..];
        var closeIdx = contentSpan.IndexOf(closeMarker.AsSpan(), StringComparison.OrdinalIgnoreCase);
        if (closeIdx < 0)
        {
            return null;
        }

        var rawTitle = WebUtility.HtmlDecode(contentSpan[..closeIdx].ToString()).Trim();
        return CollapseInlineWhitespace(rawTitle);
    }

    private static string ConvertHtmlToStructuredText(ReadOnlySpan<char> html)
    {
        if (html.IsEmpty)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(html.Length / 2);
        var i = 0;

        while (i < html.Length)
        {
            var ch = html[i];
            if (ch == '<')
            {
                // HTML comment <!-- ... -->
                if (i + 3 < html.Length && html[i + 1] == '!' && html[i + 2] == '-' && html[i + 3] == '-')
                {
                    var commentEnd = html[(i + 4)..].IndexOf("-->".AsSpan(), StringComparison.Ordinal);
                    i = commentEnd >= 0 ? i + 4 + commentEnd + 3 : html.Length;
                    continue;
                }

                var tagEndRel = html[i..].IndexOf('>');
                if (tagEndRel < 0)
                {
                    break;
                }

                var tagContent = html.Slice(i + 1, tagEndRel - 1).Trim();
                i += tagEndRel + 1;

                if (tagContent.IsEmpty)
                {
                    continue;
                }

                var isClosing = tagContent[0] == '/';
                if (isClosing)
                {
                    tagContent = tagContent[1..].TrimStart();
                }

                var nameLen = 0;
                while (nameLen < tagContent.Length && char.IsLetterOrDigit(tagContent[nameLen]))
                {
                    nameLen++;
                }

                var tagName = tagContent[..nameLen];
                if (tagName.IsEmpty)
                {
                    continue;
                }

                // Skip suppressed block elements (<script>, <style>, <svg>, <noscript>, <head>)
                if (!isClosing && IsIgnoredElement(tagName) && tagContent[^1] != '/')
                {
                    var closeTag = $"</{tagName.ToString()}>";
                    var closeRel = html[i..].IndexOf(closeTag.AsSpan(), StringComparison.OrdinalIgnoreCase);
                    i = closeRel >= 0 ? i + closeRel + closeTag.Length : html.Length;
                    continue;
                }

                HandleStructuralTag(builder, tagName, isClosing);
            }
            else
            {
                var nextTagRel = html[i..].IndexOf('<');
                var textSpan = nextTagRel >= 0 ? html.Slice(i, nextTagRel) : html[i..];
                i += textSpan.Length;

                AppendDecodedText(builder, textSpan);
            }
        }

        return NormalizeBlankLines(builder.ToString());
    }

    private static bool IsIgnoredElement(ReadOnlySpan<char> tagName) =>
        tagName.Equals("script".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
        tagName.Equals("style".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
        tagName.Equals("svg".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
        tagName.Equals("noscript".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
        tagName.Equals("head".AsSpan(), StringComparison.OrdinalIgnoreCase);

    private static void HandleStructuralTag(StringBuilder builder, ReadOnlySpan<char> tagName, bool isClosing)
    {
        if (tagName.Length == 2 && (tagName[0] == 'h' || tagName[0] == 'H') && tagName[1] is >= '1' and <= '6')
        {
            EnsureNewline(builder, 2);
            if (!isClosing)
            {
                var level = tagName[1] - '0';
                builder.Append('#', level).Append(' ');
            }
            return;
        }

        if (tagName.Equals("br".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            tagName.Equals("tr".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            EnsureNewline(builder, 1);
            return;
        }

        if (tagName.Equals("li".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            EnsureNewline(builder, 1);
            if (!isClosing)
            {
                builder.Append("- ");
            }
            return;
        }

        if (tagName.Equals("p".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            tagName.Equals("div".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            tagName.Equals("section".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            tagName.Equals("article".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            tagName.Equals("blockquote".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            tagName.Equals("pre".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            EnsureNewline(builder, 2);
            return;
        }

        if (tagName.Equals("td".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            tagName.Equals("th".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
            {
                builder.Append(' ');
            }
        }
    }

    private static void AppendDecodedText(StringBuilder builder, ReadOnlySpan<char> textSpan)
    {
        if (textSpan.IsWhiteSpace())
        {
            if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
            {
                builder.Append(' ');
            }
            return;
        }

        var decoded = textSpan.Contains('&')
            ? WebUtility.HtmlDecode(textSpan.ToString())
            : textSpan.ToString();

        var prevWasSpace = builder.Length == 0 || char.IsWhiteSpace(builder[^1]);
        for (var i = 0; i < decoded.Length; i++)
        {
            var ch = decoded[i];
            if (char.IsWhiteSpace(ch))
            {
                if (!prevWasSpace)
                {
                    builder.Append(' ');
                    prevWasSpace = true;
                }
            }
            else
            {
                builder.Append(ch);
                prevWasSpace = false;
            }
        }
    }

    private static void EnsureNewline(StringBuilder builder, int count)
    {
        while (builder.Length > 0 && builder[^1] == ' ')
        {
            builder.Length--;
        }

        if (builder.Length == 0)
        {
            return;
        }

        var existingNewlines = 0;
        for (var i = builder.Length - 1; i >= 0 && builder[i] == '\n'; i--)
        {
            existingNewlines++;
        }

        while (existingNewlines < count)
        {
            builder.Append('\n');
            existingNewlines++;
        }
    }

    private static string CollapseInlineWhitespace(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(input.Length);
        var inSpace = false;
        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (char.IsWhiteSpace(ch))
            {
                if (!inSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    inSpace = true;
                }
            }
            else
            {
                sb.Append(ch);
                inSpace = false;
            }
        }

        return sb.ToString().Trim();
    }

    private static string NormalizeBlankLines(string text)
    {
        var lines = text.Split('\n');
        var sb = new StringBuilder(text.Length);
        var consecutiveEmpty = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                consecutiveEmpty++;
                if (consecutiveEmpty <= 1 && sb.Length > 0)
                {
                    sb.Append('\n');
                }
            }
            else
            {
                consecutiveEmpty = 0;
                if (sb.Length > 0 && sb[^1] != '\n')
                {
                    sb.Append('\n');
                }
                sb.Append(line);
            }
        }

        return sb.ToString().Trim();
    }

    internal static string NormalizeMediaOrExtension(string input)
    {
        var semi = input.IndexOf(';', StringComparison.Ordinal);
        var trimmed = (semi >= 0 ? input[..semi] : input).Trim().ToLowerInvariant();
        if (!trimmed.Contains('/', StringComparison.Ordinal) && !trimmed.StartsWith('.'))
        {
            var ext = Path.GetExtension(trimmed);
            return string.IsNullOrEmpty(ext) ? $".{trimmed}" : ext;
        }

        return trimmed;
    }
}

/// <summary>
/// Composite <see cref="IDocumentReader"/> that dispatches to registered format readers (HTML, Markdown,
/// plain text, JSON, CSV, source code, or custom binary readers) and extracts heading titles when present.
/// </summary>
public sealed class CompositeDocumentReader : IDocumentReader
{
    private readonly List<IDocumentReader> _readers = [];

    /// <summary>
    /// Creates a new <see cref="CompositeDocumentReader"/> pre-populated with <see cref="HtmlDocumentReader"/>
    /// and a fallback UTF-8 text/Markdown/JSON reader.
    /// </summary>
    public CompositeDocumentReader(IEnumerable<IDocumentReader>? customReaders = null)
    {
        if (customReaders is not null)
        {
            foreach (var reader in customReaders)
            {
                if (reader is not null)
                {
                    _readers.Add(reader);
                }
            }
        }

        _readers.Add(HtmlDocumentReader.Instance);
    }

    /// <summary>
    /// Registers a custom <see cref="IDocumentReader"/> with highest priority.
    /// </summary>
    public void RegisterReader(IDocumentReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _readers.Insert(0, reader);
    }

    /// <inheritdoc />
    public bool CanRead(string contentTypeOrExtension)
    {
        if (string.IsNullOrWhiteSpace(contentTypeOrExtension))
        {
            return true;
        }

        for (var i = 0; i < _readers.Count; i++)
        {
            if (_readers[i].CanRead(contentTypeOrExtension))
            {
                return true;
            }
        }

        var normalized = HtmlDocumentReader.NormalizeMediaOrExtension(contentTypeOrExtension);
        return normalized is ".txt" or ".md" or ".markdown" or ".json" or ".jsonl" or ".csv" or ".tsv" or ".xml" or ".yaml" or ".yml" or ".cs" or ".py" or ".ts" or ".js"
            || normalized.StartsWith("text/", StringComparison.Ordinal)
            || normalized is "application/json" or "application/xml";
    }

    /// <inheritdoc />
    public async ValueTask<IngestDocument> ReadAsync(
        Stream content,
        string source,
        string? contentTypeOrExtension = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var hint = string.IsNullOrWhiteSpace(contentTypeOrExtension)
            ? Path.GetExtension(source)
            : contentTypeOrExtension;

        if (!string.IsNullOrWhiteSpace(hint))
        {
            for (var i = 0; i < _readers.Count; i++)
            {
                if (_readers[i].CanRead(hint))
                {
                    return await _readers[i].ReadAsync(content, source, hint, metadata, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        // Auto-detect HTML if no extension was provided
        var trimmedStart = text.AsSpan().TrimStart();
        if (trimmedStart.StartsWith("<!DOCTYPE html".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
            trimmedStart.StartsWith("<html".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return HtmlDocumentReader.ParseHtml(text, source, metadata);
        }

        var title = TryExtractMarkdownH1Title(text.AsSpan());
        return new IngestDocument(
            Source: source,
            Content: text,
            Title: title,
            Metadata: metadata);
    }

    private static string? TryExtractMarkdownH1Title(ReadOnlySpan<char> text)
    {
        foreach (var rawLine in text.EnumerateLines())
        {
            var line = rawLine.Trim();
            if (line.IsEmpty)
            {
                continue;
            }

            if (line.StartsWith("# ".AsSpan(), StringComparison.Ordinal))
            {
                var heading = line[2..].Trim();
                return heading.IsEmpty ? null : heading.ToString();
            }

            break;
        }

        return null;
    }
}

/// <summary>
/// Convenience extensions on <see cref="IDocumentIngestor"/> for ingesting files and streams directly via <see cref="IDocumentReader"/>.
/// </summary>
public static class DocumentIngestorReaderExtensions
{
    /// <summary>
    /// Reads <paramref name="content"/> using <paramref name="reader"/> (or <see cref="CompositeDocumentReader"/> when <see langword="null"/>)
    /// and ingests the resulting document into <paramref name="ns"/>.
    /// </summary>
    public static async ValueTask<DocumentIngestionResult> IngestStreamAsync(
        this IDocumentIngestor ingestor,
        string ns,
        Stream content,
        string source,
        string? contentTypeOrExtension = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        TextChunkingOptions? chunkingOptions = null,
        IDocumentReader? reader = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ingestor);
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var activeReader = reader ?? new CompositeDocumentReader();
        var document = await activeReader.ReadAsync(content, source, contentTypeOrExtension, metadata, cancellationToken).ConfigureAwait(false);
        if (chunkingOptions is not null)
        {
            document = document with { ChunkingOptions = chunkingOptions };
        }

        return await ingestor.IngestAsync(ns, document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a local file from <paramref name="filePath"/> using <paramref name="reader"/> (or <see cref="CompositeDocumentReader"/>)
    /// and ingests the extracted document into <paramref name="ns"/>.
    /// </summary>
    public static async ValueTask<DocumentIngestionResult> IngestFileAsync(
        this IDocumentIngestor ingestor,
        string ns,
        string filePath,
        string? source = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        TextChunkingOptions? chunkingOptions = null,
        IDocumentReader? reader = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ingestor);
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fullPath = Path.GetFullPath(filePath);
        var documentSource = string.IsNullOrWhiteSpace(source)
            ? new Uri(fullPath).AbsoluteUri
            : source;

        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await ingestor.IngestStreamAsync(
            ns: ns,
            content: stream,
            source: documentSource,
            contentTypeOrExtension: Path.GetExtension(fullPath),
            metadata: metadata,
            chunkingOptions: chunkingOptions,
            reader: reader,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
