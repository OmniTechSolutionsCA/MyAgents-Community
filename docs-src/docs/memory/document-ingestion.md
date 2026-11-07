# Document Ingestion & Chunking

The `MyAgents.Memory` package includes built-in document readers and chunkers that ingest structured and unstructured files with **zero external parsing dependencies**.

## Supported Document Formats

- **Markdown (`.md`)**: Preserves heading hierarchy (`#`, `##`, `###`), list items, and code blocks.
- **HTML (`.html`, `.htm`)**: Extracts title tags, removes navigation/script boilerplates, and strips tags while preserving structure.
- **Plain Text (`.txt`)**: Splits text with sentence-boundary and paragraph awareness.
- **JSON (`.json`)**: Splits JSON objects or array elements into discrete searchable records.

---

## 1. Registering Document Ingestion

```csharp
using Microsoft.Extensions.DependencyInjection;
using MyAgents.Memory.Ingestion;

var services = new ServiceCollection();

services.AddDocumentIngestion(builder =>
{
    // Configure default chunk size and overlap in tokens/characters
    builder.WithChunkingOptions(new TextChunkingOptions(
        ChunkSize: 1000,
        ChunkOverlap: 100,
        PreserveHeadings: true));

    // Register format-specific chunkers
    builder.UseMarkdownChunker();
    builder.UseHtmlChunker();
    builder.UsePlainTextChunker();
});

var provider = services.BuildServiceProvider();
var ingestor = provider.GetRequiredService<IDocumentIngestor>();
```

---

## 2. Ingesting Files and Streams

### Ingest a Local File
```csharp
var result = await ingestor.IngestFileAsync(
    filePath: "docs/architecture.md",
    targetNamespace: "project-knowledge",
    metadata: new Dictionary<string, string>
    {
        ["author"] = "Platform Team",
        ["version"] = "1.0.0"
    },
    cancellationToken: cancellationToken);

Console.WriteLine($"Ingested {result.DocumentId} into {result.ChunkCount} chunks.");
```

### Ingest an In-Memory Stream
```csharp
using var stream = File.OpenRead("webpage.html");

var result = await ingestor.IngestStreamAsync(
    stream: stream,
    contentType: "text/html",
    documentId: "release-notes-v1",
    targetNamespace: "project-knowledge",
    cancellationToken: cancellationToken);
```

---

## 3. Chunking Strategies

The chunker automatically guarantees that chunks respect natural content boundaries:
- Markdown chunking never splits inside a fenced code block (` ``` `).
- Heading paths (`# Guide > ## Architecture`) are prepended to each chunk as context metadata to improve semantic search relevance.
- Token counts are calculated accurately without requiring heavyweight native tokenizers.
