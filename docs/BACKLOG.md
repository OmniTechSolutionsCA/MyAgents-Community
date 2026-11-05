# Backlog

Bounded, story-shaped items feeding the `next-task` skill. Keep every item S-sized (single session); split anything larger and record follow-on slices inside the item.

**Theme: RAG ingestion primitives.** `MyAgents.Memory` already ships dense vector search, BM25, RRF hybrid fusion and the `RetrievalAugmentationFeature` middleware. What is missing is the *ingestion* half: getting documents in, and keeping the lexical index honest. These items fill that gap only. Query transformation, reranking, and vendor connectors are deliberately out of scope — see `## Park`.

**Standing cost per item:** any item that changes a public surface also requires regenerating the affected baseline under `community/tests/MyAgents.Community.Tests/Baselines/` via `eng/PublicApiSnapshotTool`, or `PublicApiApprovalTests` fails. Budget for it.

---

## Now

*(All current RAG ingestion vertical slices completed — see `## Done`.)*

---

## Next

*(All queued Next items completed — see `## Done`.)*

---

## Later

- **`dataingestion-adapter`: optional `Microsoft.Extensions.DataIngestion` adapter over `IDocumentReader`** for PDF/DOCX extraction once `Microsoft.Extensions.DataIngestion` exits preview on NuGet (currently `10.11.0-preview.1.26513.7`). Core HTML/Markdown/Text/JSON stream and file ingestion now ships zero-dependency via `IDocumentReader`, `HtmlDocumentReader`, and `CompositeDocumentReader`.
- **`episodic-consolidation-tuning`**: benchmark and tune `ConsolidateEpisodesAsync` salience thresholds against the existing BenchmarkDotNet suite.

---

## Park

- **Query transformation (HyDE, multi-query), rerankers, citation-graph assembly** — high-maintenance, benchmark-chasing territory that competes directly with LlamaIndex and Semantic Kernel. Out of character for an orchestration framework; parked pending an explicit product decision.
- **Vendor connectors (Qdrant, Azure AI Search, Pinecone)** in the Community distribution — conflicts with the stated Community boundary ("memory abstractions and local in-memory integration", `README.md:99`). Belongs behind `IVectorMemoryStore` in the proprietary distribution. Awaiting owner confirmation.
- **`HealthcareClinicalCopilot` sample** — appears in `community/samples/` as an empty directory (untracked in git). Either delete or populate; awaiting user decision.

---

## Done

- **`fix-lexical-index-seed-cap`**: Removed the 500-entry seed cap in `HybridMemoryRetriever.EnsureIndexPopulatedAsync` and added `IHybridMemoryRetriever.RefreshLexicalIndexAsync`.
- **`text-chunker`**: Added `ITextChunker`, `TextChunk`, `TextChunkingOptions`, and `RecursiveCharacterTextChunker` with boundary-preserving splits and lossless overlap reconstruction.
- **`document-ingestor`**: Added `IDocumentIngestor` (`IngestAsync`, `IngestBatchAsync`, `DeleteDocumentAsync`), `DefaultDocumentIngestor`, `DocumentIngestionBuilder`, `MemoryServiceExtensions.AddDocumentIngestion`, and `IVectorMemoryStore.SetBatchAsync` for single-batch embedding, provenance metadata tagging, atomic replace-by-source, and BM25 lexical indexing.
- **`citation-rendering`**: Upgraded `RetrievalAugmentationMiddleware.BuildRetrievedContextMessage` to render numbered citations (`[1]`, `[2]`) carrying `source`, `title`, `chunk`, `score`, and content snippet.
- **`context-budget`**: Added `MaxContextCharacters`, `MaxContextTokens`, and `DeduplicateAcrossTurns` to `RetrievalAugmentationOptions` and `RetrievalAugmentationMiddleware`.
- **`stale-index-on-retention`**: `MemoryRetentionService` now accepts an optional `IHybridMemoryRetriever` (wired via DI) and automatically removes deleted entries and indexes generated summary entries in BM25.
- **`agentic-knowledge-base-tools`**: Added `KnowledgeBaseFeature`, `SearchKnowledgeBaseTool` (`search_knowledge_base`), and `IngestDocumentTool` (`ingest_knowledge_document`).
- **`pre-filtering`**: Added metadata pre-filtering before top-K candidate selection in `BM25Index.Search`, `IVectorMemoryStore.VectorSearchAsync`, `VectorMemoryStore`, `HybridMemoryRetriever`, and `DefaultMemoryRetriever`.
- **`token-aware-chunking-and-budgets`**: Added `ApproximateTokenCounter` implementing `ITokenCounter`, `TextChunkingOptions.MaxTokens`, `RetrievalAugmentationOptions.MaxContextTokens`, and `DocumentIngestionBuilder.UseTokenCounter`.
- **`vector-store-snapshots`**: Added `SaveSnapshotAsync`, `LoadSnapshotAsync`, `SaveToFileAsync`, and `LoadFromFileAsync` on `VectorMemoryStore` plus `RagIngestionBenchmarks`.
- **`memory-store-change-notification`**: Added `IMemoryStoreChangeNotifier`, `MemoryStoreChangedEvent`, and `MemoryStoreChangeKind` in `MyAgents.Core.Memory`, implemented on `InMemoryMemoryStore` and `VectorMemoryStore`, with automatic subscription in `HybridMemoryRetriever`.
- **`citation-grounding-verification`**: Added `CitationGrounder`, `GroundedCitation`, `CitationGroundingResult`, `RetrievalAugmentationOptions.OnCitationsEvaluated`, and `RetrievalAugmentationOptions.AppendCitationReferences`.
- **`adjacent-chunk-window`**: Added `RetrievalAugmentationOptions.AdjacentChunkWindow` for neighbor chunk window expansion with automatic overlap deduplication.
- **`markdown-text-chunker`**: Added `MarkdownTextChunker` and `DocumentIngestionBuilder.UseMarkdownChunker` for heading-aware and fenced-code-block-preserving Markdown splitting.
- **`rag-memory-performance-optimizations`**: Implemented 5 high-impact performance optimizations:
  1. $O(N^2) \to O(N)$ candidate window slicing in `RecursiveCharacterTextChunker.FindSplitEnd` restricting candidate separator search to `[minSplit - sep.Length, maxEnd]`.
  2. Single-pass token budget cutoff in `RecursiveCharacterTextChunker.ConstrainMaxEndByTokenBudget` using zero-allocation `ApproximateTokenCounter.FindMaxLengthForTokenBudget`.
  3. Pre-normalized embeddings in `VectorMemoryStore` on insertion/load (`Normalize`) replacing 3-reduction + square-root `TensorPrimitives.CosineSimilarity` with a single SIMD `TensorPrimitives.Dot` during `VectorSearchAsync`.
  4. Elimination of redundant BM25 indexing passes during `DefaultDocumentIngestor.IngestBatchAsync` and `DeleteDocumentAsync` when the underlying store implements `IMemoryStoreChangeNotifier`.
  5. Zero-regex span-based tokenization in `BM25Index.Tokenize` and SIMD-accelerated span `SequenceEqual` overlap matching in `RetrievalAugmentationMiddleware` / `AdjacentChunkWindow` neighbor expansion.
- **`content-hash-embedding-deduplication`**: Added unchanged-chunk embedding reuse in `VectorMemoryStore`, `SqliteMemoryStore`, and `DefaultDocumentIngestor`, plus `CachingEmbeddingGenerator` for bounded SHA-256 content-hash caching across batches and repeated queries.
- **`sqlite-memory-store`**: Added zero-dependency `SqliteMemoryStore` (`AddSqliteMemoryStore`) implementing `IVectorMemoryStore` and `IMemoryStoreChangeNotifier` with WAL-mode SQLite persistence of entries, JSON metadata, and binary float embeddings alongside in-memory SIMD `TensorPrimitives.Dot` search.
- **`vectorDataMemoryStore`**: Added `VectorDataMemoryStore`, `VectorDataMemoryRecord`, and `MemoryServiceExtensions.AddVectorDataMemoryStore` over stable `Microsoft.Extensions.VectorData.Abstractions` (`10.0.0`), bridging any `VectorStoreCollection<string, VectorDataMemoryRecord>` to `IVectorMemoryStore` and `IMemoryStoreChangeNotifier` with unchanged-chunk embedding reuse and pre-normalized vectors.
- **`zero-dependency-document-readers`**: Added `IDocumentReader`, `HtmlDocumentReader`, `CompositeDocumentReader`, `DocumentIngestionBuilder.UseDocumentReader`, and `DocumentIngestorReaderExtensions` (`IngestStreamAsync`, `IngestFileAsync`) for zero-dependency HTML-to-Markdown, Markdown front-matter/H1, plain text, and JSON document extraction.

