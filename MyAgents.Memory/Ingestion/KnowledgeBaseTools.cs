using System.Text.Json;
using MyAgents.Core;
using MyAgents.Core.Tools;

namespace MyAgents.Memory;

/// <summary>
/// Tool that allows an agent to actively search a knowledge base namespace using hybrid semantic + BM25 retrieval
/// and receive numbered citations with provenance metadata.
/// </summary>
public sealed class SearchKnowledgeBaseTool : ITool
{
    private readonly IHybridMemoryRetriever _retriever;
    private readonly string _namespace;

    public SearchKnowledgeBaseTool(
        IHybridMemoryRetriever retriever,
        string ns = KnowledgeBaseFeature.DefaultNamespace)
    {
        _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        _namespace = ns;
    }

    public string Name => "search_knowledge_base";

    public string? Description =>
        "Searches the indexed knowledge base using hybrid semantic and BM25 lexical retrieval and returns ranked citations with source provenance.";

    public string? ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "Search query to find relevant document chunks."
            },
            "source": {
              "type": "string",
              "description": "Optional source URI filter to restrict search to a specific document."
            },
            "max_results": {
              "type": "integer",
              "description": "Maximum number of citations to return (default: 5)."
            }
          },
          "required": ["query"]
        }
        """;

    public async ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonArguments);
            if (!doc.RootElement.TryGetProperty("query", out var queryProp))
            {
                return JsonSerializer.Serialize(new { status = "error", error = "query parameter is required." });
            }

            var queryText = queryProp.GetString();
            if (string.IsNullOrWhiteSpace(queryText))
            {
                return JsonSerializer.Serialize(new { status = "error", error = "query parameter is required." });
            }

            var sourceFilter = doc.RootElement.TryGetProperty("source", out var srcProp) ? srcProp.GetString() : null;
            var maxResults = doc.RootElement.TryGetProperty("max_results", out var maxProp) && maxProp.TryGetInt32(out var m) && m > 0
                ? m
                : 5;

            IReadOnlyDictionary<string, string>? metadataFilters = !string.IsNullOrWhiteSpace(sourceFilter)
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [DocumentMetadataKeys.Source] = sourceFilter,
                }
                : null;

            var hybridQuery = new HybridMemoryQuery(
                QueryText: queryText,
                MaxResults: maxResults,
                MetadataFilters: metadataFilters);

            var results = await _retriever.SearchHybridAsync(_namespace, hybridQuery, cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Serialize(new
            {
                status = "success",
                count = results.Count,
                citations = results.Select((r, idx) =>
                {
                    var meta = r.Entry.Metadata;
                    var source = meta is not null && meta.TryGetValue(DocumentMetadataKeys.Source, out var s) ? s : r.Entry.Key;
                    var title = meta is not null && meta.TryGetValue(DocumentMetadataKeys.DocumentTitle, out var t) ? t : source;
                    var chunkIdx = meta is not null && meta.TryGetValue(DocumentMetadataKeys.ChunkIndex, out var ci) ? ci : null;
                    var chunkCount = meta is not null && meta.TryGetValue(DocumentMetadataKeys.ChunkCount, out var cc) ? cc : null;

                    return new
                    {
                        citation = idx + 1,
                        key = r.Entry.Key,
                        source,
                        title,
                        chunk_index = chunkIdx,
                        chunk_count = chunkCount,
                        score = r.FusedScore,
                        content = r.Entry.Content,
                    };
                }),
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return JsonSerializer.Serialize(new { status = "error", error = ex.Message });
        }
    }
}

/// <summary>
/// Tool that allows an agent to ingest or update a document in the knowledge base with provenance metadata.
/// </summary>
public sealed class IngestDocumentTool : ITool
{
    private readonly IDocumentIngestor _ingestor;
    private readonly string _namespace;

    public IngestDocumentTool(
        IDocumentIngestor ingestor,
        string ns = KnowledgeBaseFeature.DefaultNamespace)
    {
        _ingestor = ingestor ?? throw new ArgumentNullException(nameof(ingestor));
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        _namespace = ns;
    }

    public string Name => "ingest_knowledge_document";

    public string? Description =>
        "Chunks, embeds, and indexes a document with source provenance into the knowledge base, replacing any prior version from the same source.";

    public string? ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "source": {
              "type": "string",
              "description": "Unique source URI or identifier for the document."
            },
            "content": {
              "type": "string",
              "description": "Full text content of the document to ingest."
            },
            "title": {
              "type": "string",
              "description": "Optional human-readable document title."
            }
          },
          "required": ["source", "content"]
        }
        """;

    public async ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonArguments);
            var source = doc.RootElement.TryGetProperty("source", out var srcProp) ? srcProp.GetString() : null;
            var content = doc.RootElement.TryGetProperty("content", out var contentProp) ? contentProp.GetString() : null;

            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(content))
            {
                return JsonSerializer.Serialize(new { status = "error", error = "source and content are required." });
            }

            var title = doc.RootElement.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;

            var result = await _ingestor.IngestAsync(
                _namespace,
                new IngestDocument(Source: source, Content: content, Title: title),
                cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Serialize(new
            {
                status = "success",
                source = result.Source,
                chunk_count = result.Chunks.Count,
                replaced_chunk_count = result.ReplacedChunkCount,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return JsonSerializer.Serialize(new { status = "error", error = ex.Message });
        }
    }
}

/// <summary>
/// Agent feature that registers knowledge base RAG tools (<c>search_knowledge_base</c> and optionally
/// <c>ingest_knowledge_document</c>) into an agent assembly.
/// </summary>
public sealed class KnowledgeBaseFeature : IAgentFeature
{
    public const string DefaultNamespace = "knowledge-base";

    private readonly IHybridMemoryRetriever _retriever;
    private readonly IDocumentIngestor? _ingestor;
    private readonly string _namespace;

    public KnowledgeBaseFeature(
        IHybridMemoryRetriever retriever,
        IDocumentIngestor? ingestor = null,
        string ns = DefaultNamespace)
    {
        _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        _ingestor = ingestor;
        _namespace = ns;
    }

    public void Contribute(AgentAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        assembly.Tools.Add(new SearchKnowledgeBaseTool(_retriever, _namespace));
        if (_ingestor is not null)
        {
            assembly.Tools.Add(new IngestDocumentTool(_ingestor, _namespace));
        }

        assembly.Capabilities.Add(AgentCapabilities.Memory);
        assembly.Capabilities.Add(AgentCapabilities.Tools);
    }
}
