# Document Ingestion, Chunking & Knowledge Base Tools

The `MyAgents.Memory` package includes a complete document ingestion pipeline with **zero third-party parsing dependencies**, boundary-aware chunkers, atomic source replacement, and agentic knowledge base tools.

## Supported Document Formats (`IDocumentReader`)

`CompositeDocumentReader` and `HtmlDocumentReader` extract clean text and document titles directly from streams or files:
- **Markdown (`.md`, `text/markdown`)**: Extracts YAML front-matter `title:` or the first `# H1` heading and preserves heading hierarchy and fenced code blocks.
- **HTML (`.html`, `.htm`, `text/html`)**: Extracts `<title>` or `<h1>`, strips `<script>` / `<style>` / `<nav>` boilerplate, converts headings/lists/code/links into structured Markdown, and decodes HTML entities.
- **Plain Text (`.txt`, `text/plain`)**: Reads UTF-8 text with paragraph and sentence-boundary chunking.
- **JSON (`.json`, `application/json`)**: Formats and extracts structured JSON content.

---

## 1. Configuring Document Ingestion in DI

Use `AddDocumentIngestion` to register `IDocumentIngestor`, `IDocumentReader`, `ITextChunker`, `ITokenCounter`, and `KnowledgeBaseFeature`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MyAgents.Memory;

var services = new ServiceCollection();
services.AddSqliteMemoryStore("knowledge.db");

services.AddDocumentIngestion(builder =>
{
    // Configure chunk size, overlap, and optional token budget per chunk
    builder.WithChunkingOptions(new TextChunkingOptions(
        ChunkSize: 1000,
        ChunkOverlap: 150,
        MaxTokens: 256));

    // Use heading-aware Markdown chunker (prepends "# H1 > ## H2" breadcrumbs to chunks)
    builder.UseMarkdownChunker(includeHeadingBreadcrumbs: true);

    // Use zero-allocation approximate token counter (or register a custom ITokenCounter)
    builder.UseTokenCounter(ApproximateTokenCounter.Instance);
});

await using var provider = services.BuildServiceProvider();
var ingestor = provider.GetRequiredService<IDocumentIngestor>();
```

---

## 2. Ingesting Files, Streams, and Batches

### Ingest a Local File (`IngestFileAsync`)

```csharp
DocumentIngestionResult fileResult = await ingestor.IngestFileAsync(
    ns: "project-docs",
    filePath: "docs/architecture.md",
    source: "docs/architecture.md",
    metadata: new Dictionary<string, string>
    {
        ["version"] = "1.0.0",
        ["team"] = "platform"
    },
    cancellationToken: cancellationToken);

Console.WriteLine($"Ingested '{fileResult.Source}' into {fileResult.Chunks.Count} chunks (replaced {fileResult.ReplacedChunkCount} prior chunks).");
```

### Ingest a Stream (`IngestStreamAsync`)

```csharp
await using var htmlStream = File.OpenRead("release-notes.html");

DocumentIngestionResult streamResult = await ingestor.IngestStreamAsync(
    ns: "project-docs",
    content: htmlStream,
    source: "https://example.com/release-notes",
    contentTypeOrExtension: "text/html",
    cancellationToken: cancellationToken);
```

### Batch Ingestion & Atomic Source Replacement (`IngestBatchAsync`)

Re-ingesting a document with the same `Source` automatically removes stale chunks, reuses embeddings for unchanged chunks (via SHA-256 content hashes), and updates the `BM25Index`:

```csharp
BatchDocumentIngestionResult batchResult = await ingestor.IngestBatchAsync(
    ns: "project-docs",
    documents:
    [
        new IngestDocument(Source: "guide-1", Content: "# Intro\nWelcome to MyAgents.", Title: "Intro Guide"),
        new IngestDocument(Source: "guide-2", Content: "# Security\nAlways configure tool policies.", Title: "Security Guide")
    ],
    cancellationToken: cancellationToken);

Console.WriteLine($"Total chunks: {batchResult.TotalChunks}, Replaced: {batchResult.TotalReplacedChunks}");

// Delete all chunks for a document source
int deletedChunks = await ingestor.DeleteDocumentAsync("project-docs", source: "guide-1", cancellationToken);
```

---

## 3. Agentic Knowledge Base Tools (`KnowledgeBaseFeature`)

In addition to passive middleware injection (`RetrievalAugmentationFeature`), you can give an agent active tools to search and ingest documents on demand using `KnowledgeBaseFeature`:

- `search_knowledge_base`: Executes a hybrid semantic + BM25 search with optional `source` filtering and returns numbered citations with provenance metadata.
- `ingest_knowledge_document`: Chunks, embeds, and indexes a document into the knowledge base namespace.

```csharp
using MyAgents.Core;
using MyAgents.Memory;

var kbFeature = provider.GetRequiredService<KnowledgeBaseFeature>();

var definition = new AgentDefinition("knowledge-curator", [
    kbFeature
]);
```
