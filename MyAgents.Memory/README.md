# MyAgents.Memory

Memory abstractions, local vector + BM25 hybrid retrieval, document ingestion, and grounded
citation augmentation for MyAgents Community.

## Capabilities

- **Pluggable, Markdown & Token-Aware Text Chunking (`ITextChunker`)**: `RecursiveCharacterTextChunker`
  splits long documents into size-bounded, overlapping chunks along paragraph, line, sentence, and word
  boundaries; `MarkdownTextChunker` splits along `#`–`######` headings, preserves fenced code blocks
  intact, and prepends ancestor heading breadcrumbs; both support character (`ChunkSize`) and token
  (`MaxTokens`) budgets via `ITokenCounter` and `ApproximateTokenCounter`.
- **Document Ingestion (`IDocumentIngestor`)**: `DefaultDocumentIngestor` chunks documents, batches
  embeddings in a single `IEmbeddingGenerator` call (`IngestAsync` and `IngestBatchAsync`), tags chunks
  with provenance metadata (`source`, `document_title`, `chunk_index`, `chunk_count`), replaces prior
  chunks on re-ingest, removes retired sources (`DeleteDocumentAsync`), and synchronizes the BM25 lexical index.
- **Hybrid Retrieval, Pre-Filtering & Reactive Sync (`IHybridMemoryRetriever`)**: Combines SIMD-accelerated
  dense cosine similarity (`VectorMemoryStore`) with sparse lexical ranking (`BM25Index`) via Reciprocal
  Rank Fusion (RRF), pushing `MetadataFilters` into both engines before top-K candidate selection and
  automatically synchronizing BM25 via `IMemoryStoreChangeNotifier` and `MemoryRetentionService`.
- **Grounded Citations, Window Expansion & Verification (`RetrievalAugmentationFeature`)**: Injects numbered
  citations with source URIs, titles, chunk positions, and relevance scores into the agent turn context,
  supporting `MaxContextCharacters` and `MaxContextTokens` budgets, `DeduplicateAcrossTurns`,
  `AdjacentChunkWindow` neighbor expansion, and post-turn `CitationGrounder` verification
  (`OnCitationsEvaluated` and `AppendCitationReferences`).
- **Durable Vector Snapshots, SQLite Store & `Microsoft.Extensions.VectorData` Bridge (`VectorMemoryStore`, `SqliteMemoryStore`, `VectorDataMemoryStore`)**:
  `VectorMemoryStore` supports `SaveSnapshotAsync` / `LoadSnapshotAsync` and `SaveToFileAsync` /
  `LoadFromFileAsync` for JSON snapshot persistence; `SqliteMemoryStore` (`AddSqliteMemoryStore`)
  provides transactional WAL-mode SQLite persistence of entries, JSON metadata, and binary float embeddings
  alongside an in-memory cache for microsecond SIMD dot-product search; and `VectorDataMemoryStore`
  (`AddVectorDataMemoryStore`, `VectorDataMemoryRecord`) bridges any `Microsoft.Extensions.VectorData`
  `VectorStoreCollection<string, VectorDataMemoryRecord>` to `IVectorMemoryStore` and `IMemoryStoreChangeNotifier`
  with zero vendor SDK lock-in.
- **Zero-Dependency Document Readers & Stream/File Ingestion (`IDocumentReader`, `HtmlDocumentReader`, `CompositeDocumentReader`)**:
  `IngestStreamAsync` and `IngestFileAsync` extract clean text and titles from HTML (converting headings,
  lists, and `<pre><code>` blocks to Markdown while stripping `<script>`/`<style>`), Markdown (extracting
  YAML front-matter or H1 titles), plain text, and JSON without third-party parser dependencies, while
  allowing custom format readers (such as PDF/DOCX or `Microsoft.Extensions.DataIngestion` adapters) via
  `DocumentIngestionBuilder.UseDocumentReader`.
- **Content-Hash Embedding Deduplication (`CachingEmbeddingGenerator`)**: `VectorMemoryStore`,
  `SqliteMemoryStore`, and `VectorDataMemoryStore` automatically reuse existing normalized embeddings when
  re-ingesting unchanged chunks, and `CachingEmbeddingGenerator` provides bounded SHA-256 content-hash
  caching across repeated queries and batches.
- **Agentic Knowledge Base Tools (`KnowledgeBaseFeature`)**: Contributes `search_knowledge_base` and
  `ingest_knowledge_document` tools so agents can actively query or update the corpus on demand.

```csharp
services.AddSingleton<IMemoryStore>(sp =>
    new VectorMemoryStore(sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()));
services.AddDocumentIngestion(builder =>
{
    builder.WithChunkingOptions(new TextChunkingOptions(ChunkSize: 1000, ChunkOverlap: 100));
});
```

See the root README for the Community feature boundary and local build instructions.
