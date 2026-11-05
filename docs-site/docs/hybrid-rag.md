# Hybrid RAG & Memory

`MyAgents.Memory` provides a complete retrieval-augmented generation (RAG) system with zero external vendor SDK dependencies.

## Key Capabilities

1. **Hybrid Retrieval**: Combines SIMD dense cosine similarity (`TensorPrimitives.Dot`) with BM25 sparse lexical ranking via Reciprocal Rank Fusion (RRF).
2. **Zero-Dependency Document Readers**: Ingest HTML, Markdown, Plain Text, and JSON streams directly without third-party parser dependencies.
3. **Reactive Index Synchronization**: Uses `IMemoryStoreChangeNotifier` to keep the BM25 lexical index in sync automatically when vector records change.
4. **Grounded Citations**: Formats citations (`[1]`, `[2]`), enforces token context budgets, expands adjacent chunk windows, and verifies factual grounding.

## Configuration

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using MyAgents.Memory;
using MyAgents.Memory.Ingestion;

var services = new ServiceCollection();

// 1. Register vector store (SqliteMemoryStore, VectorMemoryStore, or VectorDataMemoryStore)
services.AddSingleton<IMemoryStore>(sp =>
    new SqliteMemoryStore("memory.db", sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()));

// 2. Register document ingestion with markdown chunking
services.AddDocumentIngestion(builder =>
{
    builder.WithChunkingOptions(new TextChunkingOptions(ChunkSize: 1000, ChunkOverlap: 100));
    builder.UseMarkdownChunker();
});
```

## Document Ingestion

Ingest files or streams with automatic title extraction and format detection:

```csharp
var ingestor = provider.GetRequiredService<IDocumentIngestor>();

// Ingest an HTML or Markdown file directly
await ingestor.IngestFileAsync(
    "knowledge/architecture-guide.html",
    cancellationToken: cancellationToken);
```

## Grounded Citation Feature

Add `RetrievalAugmentationFeature` to your agent definition to inject relevant context messages and verify model references:

```csharp
var ragFeature = new RetrievalAugmentationFeature(retriever, new RetrievalAugmentationOptions
{
    MaxResults = 5,
    MaxContextTokens = 1500,
    AdjacentChunkWindow = 1,
    AppendCitationReferences = true
});

var definition = new AgentDefinition("grounded-assistant", [ragFeature]);
```
