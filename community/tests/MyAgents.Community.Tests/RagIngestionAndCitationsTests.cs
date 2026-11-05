using System.Numerics.Tensors;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using MyAgents.Core;
using MyAgents.Core.Memory;
using MyAgents.Memory;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class RagIngestionAndCitationsTests
{
    // ── Slice 1: text-chunker ───────────────────────────────────────────────

    [Test]
    public void RecursiveCharacterTextChunker_chunks_3000_chars_with_exact_overlap_and_lossless_reconstruction()
    {
        var builder = new StringBuilder(3000);
        var index = 0;
        while (builder.Length < 3000)
        {
            var sentence = $"Paragraph section {index++:D3} discusses distributed agent retrieval architecture. ";
            var remaining = 3000 - builder.Length;
            builder.Append(sentence.Length <= remaining ? sentence : sentence[..remaining]);
        }

        var originalText = builder.ToString();
        Assert.That(originalText.Length, Is.EqualTo(3000));

        var chunker = new RecursiveCharacterTextChunker();
        var options = new TextChunkingOptions(ChunkSize: 1000, ChunkOverlap: 100);
        var chunks = chunker.Chunk(originalText, options);

        Assert.That(chunks, Has.Count.GreaterThanOrEqualTo(3));

        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.That(chunks[i].Index, Is.EqualTo(i));
            Assert.That(chunks[i].Content.Length, Is.LessThanOrEqualTo(1000));
            Assert.That(chunks[i].Length, Is.EqualTo(chunks[i].Content.Length));

            if (i > 0)
            {
                var prev = chunks[i - 1].Content;
                var curr = chunks[i].Content;
                var prevTail = prev[^100..];
                var currHead = curr[..100];
                Assert.That(currHead, Is.EqualTo(prevTail), $"Chunk {i} should share 100-character overlap with chunk {i - 1}.");
            }
        }

        var reconstructed = new StringBuilder(originalText.Length);
        reconstructed.Append(chunks[0].Content);
        for (var i = 1; i < chunks.Count; i++)
        {
            reconstructed.Append(chunks[i].Content[100..]);
        }

        Assert.That(reconstructed.ToString(), Is.EqualTo(originalText));
    }

    [Test]
    public void RecursiveCharacterTextChunker_preserves_word_and_sentence_boundaries()
    {
        const string paragraph1 = "Agents coordinate complex workflows using durable state checkpoints and structured tool invocations.";
        const string paragraph2 = "Hybrid memory combines sparse BM25 lexical scoring with dense cosine similarity over float embeddings.";
        const string paragraph3 = "Reciprocal rank fusion merges candidate lists without requiring score calibration across modalities.";
        var text = $"{paragraph1}\n\n{paragraph2}\n\n{paragraph3}";

        var chunker = new RecursiveCharacterTextChunker();
        var chunks = chunker.Chunk(text, new TextChunkingOptions(ChunkSize: 120, ChunkOverlap: 0));

        Assert.That(chunks, Has.Count.EqualTo(3));
        Assert.Multiple(() =>
        {
            for (var i = 0; i < chunks.Count - 1; i++)
            {
                var splitOffset = chunks[i].StartOffset + chunks[i].Length;
                var charBeforeSplit = text[splitOffset - 1];
                Assert.That(char.IsWhiteSpace(charBeforeSplit), Is.True, $"Split at offset {splitOffset} fell inside a word.");
            }

            Assert.That(string.Concat(chunks.Select(static c => c.Content)), Is.EqualTo(text));
        });
    }

    [Test]
    public void RecursiveCharacterTextChunker_handles_short_empty_and_whitespace_inputs()
    {
        var chunker = new RecursiveCharacterTextChunker();
        var options = new TextChunkingOptions(ChunkSize: 500, ChunkOverlap: 50);

        const string shortText = "Short single-chunk document.";
        var shortChunks = chunker.Chunk(shortText, options);

        Assert.Multiple(() =>
        {
            Assert.That(shortChunks, Has.Count.EqualTo(1));
            Assert.That(shortChunks[0].Content, Is.EqualTo(shortText));
            Assert.That(chunker.Chunk(string.Empty, options), Is.Empty);
            Assert.That(chunker.Chunk("   \t\r\n  ", options), Is.Empty);
        });
    }

    [Test]
    public async Task AddDocumentIngestion_uses_custom_ITextChunker_registered_through_builder()
    {
        var store = new InMemoryMemoryStore();
        var stubChunker = new RecordingTextChunker();

        var services = new ServiceCollection()
            .AddSingleton<IMemoryStore>(store)
            .AddDocumentIngestion(builder => builder.UseChunker(stubChunker))
            .BuildServiceProvider();

        var ingestor = services.GetRequiredService<IDocumentIngestor>();
        var result = await ingestor.IngestAsync(
            "custom-chunker-ns",
            new IngestDocument("doc://custom", "Custom chunker input text"));

        Assert.Multiple(() =>
        {
            Assert.That(stubChunker.InvocationCount, Is.EqualTo(1));
            Assert.That(stubChunker.LastInput, Is.EqualTo("Custom chunker input text"));
            Assert.That(result.Chunks, Has.Count.EqualTo(1));
            Assert.That(result.Chunks[0].Content, Is.EqualTo("[custom-chunk] Custom chunker input text"));
        });
    }

    // ── Slice 2: document-ingestor ──────────────────────────────────────────

    [Test]
    public async Task IngestAsync_stores_chunks_with_provenance_metadata_and_indexes_for_lexical_search()
    {
        var store = new CountingMemoryStore();
        var retriever = new HybridMemoryRetriever(store);
        var ingestor = new DefaultDocumentIngestor(store, hybridRetriever: retriever);

        var doc = new IngestDocument(
            Source: "https://docs.myagents.dev/architecture",
            Content: "First section about runtime loops.\n\nSecond section with uniqueingestlexicaltoken marker.\n\nThird section about observability.",
            Title: "MyAgents Architecture Guide",
            Metadata: new Dictionary<string, string> { ["category"] = "reference" },
            ChunkingOptions: new TextChunkingOptions(ChunkSize: 50, ChunkOverlap: 0));

        var ingestResult = await ingestor.IngestAsync("docs", doc);
        var searchCountBeforeHybridSearch = store.SearchCallCount;

        var searchResults = await retriever.SearchHybridAsync(
            "docs",
            new HybridMemoryQuery("uniqueingestlexicaltoken", MaxResults: 3));

        Assert.Multiple(() =>
        {
            Assert.That(ingestResult.Chunks, Has.Count.EqualTo(3));
            for (var i = 0; i < ingestResult.Chunks.Count; i++)
            {
                var chunkEntry = ingestResult.Chunks[i];
                Assert.That(chunkEntry.Metadata, Is.Not.Null);
                Assert.That(chunkEntry.Metadata![DocumentMetadataKeys.Source], Is.EqualTo("https://docs.myagents.dev/architecture"));
                Assert.That(chunkEntry.Metadata[DocumentMetadataKeys.DocumentTitle], Is.EqualTo("MyAgents Architecture Guide"));
                Assert.That(chunkEntry.Metadata[DocumentMetadataKeys.ChunkIndex], Is.EqualTo(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                Assert.That(chunkEntry.Metadata[DocumentMetadataKeys.ChunkCount], Is.EqualTo("3"));
                Assert.That(chunkEntry.Metadata["category"], Is.EqualTo("reference"));
            }

            Assert.That(searchResults, Is.Not.Empty);
            Assert.That(searchResults[0].Entry.Content, Does.Contain("uniqueingestlexicaltoken"));
            Assert.That(store.SearchCallCount, Is.EqualTo(searchCountBeforeHybridSearch), "Hybrid search should not rescan store after ingestion indexed the chunks.");
        });
    }

    [Test]
    public async Task IngestAsync_batches_all_chunk_embeddings_into_a_single_embedder_call()
    {
        var embedder = new RecordingEmbeddingGenerator();
        var vectorStore = new VectorMemoryStore(embedder);
        var retriever = new HybridMemoryRetriever(vectorStore, vectorStore);
        var ingestor = new DefaultDocumentIngestor(vectorStore, hybridRetriever: retriever, vectorStore: vectorStore);

        var doc = new IngestDocument(
            Source: "doc://batch-test",
            Content: "Chunk one content here.\n\nChunk two content here.\n\nChunk three content here.",
            ChunkingOptions: new TextChunkingOptions(ChunkSize: 30, ChunkOverlap: 0));

        var result = await ingestor.IngestAsync("batch-ns", doc);

        Assert.Multiple(() =>
        {
            Assert.That(result.Chunks, Has.Count.EqualTo(3));
            Assert.That(embedder.CallCount, Is.EqualTo(1));
            Assert.That(embedder.Batches[0], Has.Count.EqualTo(3));
        });
    }

    [Test]
    public async Task IngestAsync_replaces_previous_chunks_when_reingesting_same_source_uri()
    {
        var embedder = new RecordingEmbeddingGenerator();
        var vectorStore = new VectorMemoryStore(embedder);
        var retriever = new HybridMemoryRetriever(vectorStore, vectorStore);
        var ingestor = new DefaultDocumentIngestor(vectorStore, hybridRetriever: retriever, vectorStore: vectorStore);

        var v1 = new IngestDocument(
            Source: "doc://release-notes",
            Content: "Alpha legacyv1markerone text.\n\nBeta legacyv1markertwo text.\n\nGamma legacyv1markerthree text.",
            Title: "Release Notes v1",
            ChunkingOptions: new TextChunkingOptions(ChunkSize: 35, ChunkOverlap: 0));

        await ingestor.IngestAsync("releases", v1);

        var v2 = new IngestDocument(
            Source: "doc://release-notes",
            Content: "Updated freshv2markerone text.\n\nUpdated freshv2markertwo text.",
            Title: "Release Notes v2",
            ChunkingOptions: new TextChunkingOptions(ChunkSize: 35, ChunkOverlap: 0));

        var v2Result = await ingestor.IngestAsync("releases", v2);
        var remainingEntries = await vectorStore.SearchAsync("releases", maxResults: 100);
        var obsoleteSearch = await retriever.SearchHybridAsync(
            "releases",
            new HybridMemoryQuery("legacyv1markerthree", MaxResults: 5, DenseWeight: 0.0, SparseWeight: 1.0, MinScore: 0.001));
        var freshSearch = await retriever.SearchHybridAsync(
            "releases",
            new HybridMemoryQuery("freshv2markertwo", MaxResults: 5, DenseWeight: 0.0, SparseWeight: 1.0, MinScore: 0.001));

        Assert.Multiple(() =>
        {
            Assert.That(v2Result.ReplacedChunkCount, Is.EqualTo(3));
            Assert.That(v2Result.Chunks, Has.Count.EqualTo(2));
            Assert.That(remainingEntries, Has.Count.EqualTo(2));
            Assert.That(remainingEntries.Any(static e => e.Content.Contains("legacyv1", StringComparison.Ordinal)), Is.False);
            Assert.That(remainingEntries.All(static e => e.Metadata![DocumentMetadataKeys.DocumentTitle] == "Release Notes v2"), Is.True);
            Assert.That(obsoleteSearch, Is.Empty);
            Assert.That(freshSearch, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task IngestAsync_leaves_no_partial_chunks_when_embedder_fails_and_preserves_exception()
    {
        var expectedFailure = new InvalidOperationException("Simulated upstream embedding outage");
        var failingEmbedder = new RecordingEmbeddingGenerator { FailureToThrow = expectedFailure };
        var vectorStore = new VectorMemoryStore(failingEmbedder);
        var retriever = new HybridMemoryRetriever(vectorStore, vectorStore);
        var ingestor = new DefaultDocumentIngestor(vectorStore, hybridRetriever: retriever, vectorStore: vectorStore);

        var doc = new IngestDocument(
            Source: "doc://atomic-failure",
            Content: "First chunk of atomic test.\n\nSecond chunk of atomic test.\n\nThird chunk of atomic test.",
            ChunkingOptions: new TextChunkingOptions(ChunkSize: 32, ChunkOverlap: 0));

        var thrown = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ingestor.IngestAsync("atomic-ns", doc));

        var storedEntries = await vectorStore.SearchAsync("atomic-ns", maxResults: 100);

        Assert.Multiple(() =>
        {
            Assert.That(thrown, Is.SameAs(expectedFailure));
            Assert.That(storedEntries, Is.Empty);
        });
    }

    [Test]
    public async Task IngestAsync_middle_chunk_is_top_hybrid_result_with_intact_metadata()
    {
        var embedder = new RecordingEmbeddingGenerator();
        var vectorStore = new VectorMemoryStore(embedder);
        var retriever = new HybridMemoryRetriever(vectorStore, vectorStore);
        var ingestor = new DefaultDocumentIngestor(vectorStore, hybridRetriever: retriever, vectorStore: vectorStore);

        var doc = new IngestDocument(
            Source: "file:///handbook/security.md",
            Content: "Introduction to general workspace policies.\n\n" +
                     "Middle chunk describing quantumtokenvault777 hardware key rotation.\n\n" +
                     "Appendix listing contact emails and escalation paths.",
            Title: "Security Handbook",
            Metadata: new Dictionary<string, string> { ["owner"] = "secops" },
            ChunkingOptions: new TextChunkingOptions(ChunkSize: 75, ChunkOverlap: 0));

        await ingestor.IngestAsync("handbook", doc);

        var results = await retriever.SearchHybridAsync(
            "handbook",
            new HybridMemoryQuery("quantumtokenvault777 hardware key", MaxResults: 3));

        Assert.That(results, Is.Not.Empty);
        var top = results[0].Entry;
        Assert.Multiple(() =>
        {
            Assert.That(top.Content, Does.Contain("quantumtokenvault777"));
            Assert.That(top.Metadata, Is.Not.Null);
            Assert.That(top.Metadata![DocumentMetadataKeys.Source], Is.EqualTo("file:///handbook/security.md"));
            Assert.That(top.Metadata[DocumentMetadataKeys.DocumentTitle], Is.EqualTo("Security Handbook"));
            Assert.That(top.Metadata[DocumentMetadataKeys.ChunkIndex], Is.EqualTo("1"));
            Assert.That(top.Metadata[DocumentMetadataKeys.ChunkCount], Is.EqualTo("3"));
            Assert.That(top.Metadata["owner"], Is.EqualTo("secops"));
        });
    }

    // ── Slice 3: citation-rendering ─────────────────────────────────────────

    [Test]
    public async Task RetrievalAugmentationFeature_renders_numbered_citations_with_source_title_chunk_and_score()
    {
        var embedder = new RecordingEmbeddingGenerator();
        var vectorStore = new VectorMemoryStore(embedder);

        var services = new ServiceCollection()
            .AddSingleton<IMemoryStore>(vectorStore)
            .AddSingleton<IVectorMemoryStore>(vectorStore)
            .AddDocumentIngestion()
            .BuildServiceProvider();

        var ingestor = services.GetRequiredService<IDocumentIngestor>();
        await ingestor.IngestAsync(
            "rag-kb",
            new IngestDocument(
                Source: "https://docs.myagents.dev/workers/sqlite",
                Content: "Overview of worker hosting.\n\nSQLite leases use write transactions and fencing tokens for crash recovery.",
                Title: "SQLite Durable Workers",
                ChunkingOptions: new TextChunkingOptions(ChunkSize: 80, ChunkOverlap: 0)));

        var feature = new RetrievalAugmentationFeature(new RetrievalAugmentationOptions("rag-kb", MaxResults: 2));
        var assembly = new AgentAssembly();
        feature.Contribute(assembly);
        var pipeline = assembly.Pipeline.Build();

        IReadOnlyList<AgentMessage>? capturedMessages = null;
        using var inspectAgent = new InspectingAgent(ctx => capturedMessages = ctx.Messages);

        var turnContext = new AgentTurnContext(
            SessionId: "sess-citations",
            Messages:
            [
                new AgentMessage("system", "You are a technical assistant."),
                new AgentMessage("user", "How do SQLite leases handle crash recovery?")
            ],
            Services: new AgentTurnServices(services));

        await pipeline.InvokeAsync(inspectAgent, turnContext);

        Assert.That(capturedMessages, Is.Not.Null);
        Assert.That(capturedMessages, Has.Count.EqualTo(3));

        var ragMessage = capturedMessages![1].Content;
        Assert.Multiple(() =>
        {
            Assert.That(ragMessage, Does.StartWith("Retrieved context:"));
            Assert.That(ragMessage, Does.Contain("[1] source: https://docs.myagents.dev/workers/sqlite"));
            Assert.That(ragMessage, Does.Contain("title: SQLite Durable Workers"));
            Assert.That(ragMessage, Does.Contain("chunk: 1/2"));
            Assert.That(ragMessage, Does.Contain("score: "));
            Assert.That(ragMessage, Does.Contain("fencing tokens for crash recovery"));
        });
    }

    // ── Slice 4: Batch Ingestion, Delete-by-Source, Context Budget, Retention Sync, and Agentic Tools ──

    [Test]
    public async Task IngestBatchAsync_embeds_multiple_documents_in_single_call_and_DeleteDocumentAsync_removes_source()
    {
        var embedder = new RecordingEmbeddingGenerator();
        var vectorStore = new VectorMemoryStore(embedder);
        var retriever = new HybridMemoryRetriever(vectorStore, vectorStore);
        var ingestor = new DefaultDocumentIngestor(vectorStore, hybridRetriever: retriever, vectorStore: vectorStore);

        var docs = new[]
        {
            new IngestDocument(
                Source: "doc://guide-alpha",
                Content: "Alpha part one.\n\nAlpha part two batchalphamarker text.",
                Title: "Alpha Guide",
                ChunkingOptions: new TextChunkingOptions(ChunkSize: 40, ChunkOverlap: 0)),
            new IngestDocument(
                Source: "doc://guide-beta",
                Content: "Beta part one.\n\nBeta part two batchbetamarker text.",
                Title: "Beta Guide",
                ChunkingOptions: new TextChunkingOptions(ChunkSize: 40, ChunkOverlap: 0)),
        };

        var batchResult = await ingestor.IngestBatchAsync("batch-corpus", docs);

        Assert.Multiple(() =>
        {
            Assert.That(batchResult.Documents, Has.Count.EqualTo(2));
            Assert.That(batchResult.TotalChunks, Is.EqualTo(4));
            Assert.That(embedder.CallCount, Is.EqualTo(1), "All chunks across both documents must be embedded in a single batch call.");
            Assert.That(embedder.Batches[0], Has.Count.EqualTo(4));
        });

        var deletedCount = await ingestor.DeleteDocumentAsync("batch-corpus", "doc://guide-alpha");
        var remaining = await vectorStore.SearchAsync("batch-corpus", maxResults: 100);
        var deletedLexicalSearch = await retriever.SearchHybridAsync(
            "batch-corpus",
            new HybridMemoryQuery("batchalphamarker", MaxResults: 5, DenseWeight: 0.0, SparseWeight: 1.0, MinScore: 0.001));
        var survivingLexicalSearch = await retriever.SearchHybridAsync(
            "batch-corpus",
            new HybridMemoryQuery("batchbetamarker", MaxResults: 5, DenseWeight: 0.0, SparseWeight: 1.0, MinScore: 0.001));

        Assert.Multiple(() =>
        {
            Assert.That(deletedCount, Is.EqualTo(2));
            Assert.That(remaining, Has.Count.EqualTo(2));
            Assert.That(deletedLexicalSearch, Is.Empty);
            Assert.That(survivingLexicalSearch, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task RetrievalAugmentationFeature_enforces_MaxContextCharacters_and_DeduplicateAcrossTurns()
    {
        var store = new InMemoryMemoryStore();
        var retriever = new HybridMemoryRetriever(store);
        var ingestor = new DefaultDocumentIngestor(store, hybridRetriever: retriever);

        await ingestor.IngestAsync(
            "budget-kb",
            new IngestDocument(
                Source: "doc://budget",
                Content: "First chunk about distributed consensus algorithms and quorum leases.\n\n" +
                         "Second chunk about distributed consensus recovery and leader election.",
                Title: "Consensus Notes",
                ChunkingOptions: new TextChunkingOptions(ChunkSize: 80, ChunkOverlap: 0)));

        var services = new ServiceCollection()
            .AddSingleton<IMemoryRetriever>(retriever)
            .BuildServiceProvider();

        // Budget of 75 characters allows only the first ~70-char chunk, dropping the second chunk
        var feature = new RetrievalAugmentationFeature(new RetrievalAugmentationOptions(
            Namespace: "budget-kb",
            MaxResults: 5,
            MaxContextCharacters: 75,
            DeduplicateAcrossTurns: true));

        var assembly = new AgentAssembly();
        feature.Contribute(assembly);
        var pipeline = assembly.Pipeline.Build();

        IReadOnlyList<AgentMessage>? turn1Messages = null;
        using var inspectAgent = new InspectingAgent(ctx => turn1Messages = ctx.Messages);

        var turn1Context = new AgentTurnContext(
            SessionId: "sess-budget",
            Messages: [new AgentMessage("user", "distributed consensus")],
            Services: new AgentTurnServices(services));

        await pipeline.InvokeAsync(inspectAgent, turn1Context);

        Assert.That(turn1Messages, Is.Not.Null);
        Assert.That(turn1Messages, Has.Count.EqualTo(2));
        var turn1Rag = turn1Messages![0].Content;
        Assert.Multiple(() =>
        {
            Assert.That(turn1Rag, Does.Contain("[1] source: doc://budget"));
            Assert.That(turn1Rag, Does.Not.Contain("[2] source: doc://budget"), "Second chunk should be excluded by MaxContextCharacters budget.");
        });

        // Turn 2: carry turn1's RAG system message forward; DeduplicateAcrossTurns skips chunk 1 and selects chunk 2
        IReadOnlyList<AgentMessage>? turn2Messages = null;
        using var inspectAgent2 = new InspectingAgent(ctx => turn2Messages = ctx.Messages);

        var turn2Context = new AgentTurnContext(
            SessionId: "sess-budget",
            Messages:
            [
                turn1Messages[0],
                new AgentMessage("user", "distributed consensus")
            ],
            Services: new AgentTurnServices(services));

        await pipeline.InvokeAsync(inspectAgent2, turn2Context);

        Assert.That(turn2Messages, Is.Not.Null);
        Assert.That(turn2Messages, Has.Count.EqualTo(3));
        var turn2Rag = turn2Messages![1].Content;
        Assert.That(turn2Rag, Does.Not.EqualTo(turn1Rag), "Second turn should deduplicate the chunk already injected in turn 1.");
    }

    [Test]
    public async Task MemoryRetentionService_automatically_removes_deleted_entries_and_indexes_summary_in_hybrid_retriever()
    {
        var store = new InMemoryMemoryStore();
        var retriever = new HybridMemoryRetriever(store);

        var expired1 = new MemoryEntry("exp-1", "auto-ret", "expiredautoretentiontoken alpha", CreatedAt: DateTimeOffset.UtcNow.AddDays(-5));
        var expired2 = new MemoryEntry("exp-2", "auto-ret", "overflowautoretentiontoken beta", CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var fresh = new MemoryEntry("fresh-1", "auto-ret", "fresh active memory entry", CreatedAt: DateTimeOffset.UtcNow);

        await store.SetAsync(expired1);
        await store.SetAsync(expired2);
        await store.SetAsync(fresh);
        retriever.IndexLexicalEntry("auto-ret", expired1);
        retriever.IndexLexicalEntry("auto-ret", expired2);
        retriever.IndexLexicalEntry("auto-ret", fresh);

        var retention = new MemoryRetentionService(
            store,
            new StubSummaryGenerator("compactedautoretsummarytoken consolidated memory"),
            retriever);

        // No manual RefreshLexicalIndexAsync call — retention syncs the lexical index directly
        var result = await retention.ApplyAsync(
            "auto-ret",
            new MemoryRetentionPolicy(
                MaxEntries: 1,
                MaxAge: TimeSpan.FromDays(1),
                SummarizeTrimmedEntries: true));

        var expiredHits = await retriever.SearchHybridAsync("auto-ret", new HybridMemoryQuery("expiredautoretentiontoken", MaxResults: 5));
        var overflowHits = await retriever.SearchHybridAsync("auto-ret", new HybridMemoryQuery("overflowautoretentiontoken", MaxResults: 5));
        var summaryHits = await retriever.SearchHybridAsync("auto-ret", new HybridMemoryQuery("compactedautoretsummarytoken", MaxResults: 5));

        Assert.Multiple(() =>
        {
            Assert.That(result.DeletedCount, Is.EqualTo(2));
            Assert.That(result.SummaryEntry, Is.Not.Null);
            Assert.That(expiredHits, Is.Empty);
            Assert.That(overflowHits, Is.Empty);
            Assert.That(summaryHits, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task KnowledgeBaseFeature_contributes_ingest_and_search_tools_returning_grounded_citations()
    {
        var store = new InMemoryMemoryStore();
        var retriever = new HybridMemoryRetriever(store);
        var ingestor = new DefaultDocumentIngestor(store, hybridRetriever: retriever);

        var feature = new KnowledgeBaseFeature(retriever, ingestor, "agentic-kb");
        var assembly = new AgentAssembly();
        feature.Contribute(assembly);

        var tools = assembly.Tools.Build();
        var ingestTool = tools.FirstOrDefault(static t => t.Name == "ingest_knowledge_document");
        var searchTool = tools.FirstOrDefault(static t => t.Name == "search_knowledge_base");

        Assert.Multiple(() =>
        {
            Assert.That(ingestTool, Is.Not.Null);
            Assert.That(searchTool, Is.Not.Null);
        });

        var ingestJson = await ingestTool!.InvokeAsync("""
            {
              "source": "kb://runbooks/failover",
              "title": "Database Failover Runbook",
              "content": "Promote the standby replica using pg_ctl promote and verify fencing."
            }
            """);

        Assert.That(ingestJson, Does.Contain("\"status\":\"success\""));
        Assert.That(ingestJson, Does.Contain("\"chunk_count\":1"));

        var searchJson = await searchTool!.InvokeAsync("""
            {
              "query": "standby replica pg_ctl promote",
              "source": "kb://runbooks/failover",
              "max_results": 3
            }
            """);

        Assert.Multiple(() =>
        {
            Assert.That(searchJson, Does.Contain("\"status\":\"success\""));
            Assert.That(searchJson, Does.Contain("kb://runbooks/failover"));
            Assert.That(searchJson, Does.Contain("Database Failover Runbook"));
            Assert.That(searchJson, Does.Contain("pg_ctl promote"));
        });
    }

    // ── Slice 5: Pre-Filtering, Token-Aware Chunking/Budgets, and Vector Snapshots ──

    [Test]
    public async Task SearchHybridAsync_prefilters_by_metadata_before_top_k_candidate_cutoff()
    {
        var embedder = new RecordingEmbeddingGenerator();
        var vectorStore = new VectorMemoryStore(embedder);
        var retriever = new HybridMemoryRetriever(vectorStore, vectorStore);

        var entries = new List<MemoryEntry>(60);
        for (var i = 0; i < 60; i++)
        {
            var isTarget = i == 55;
            entries.Add(new MemoryEntry(
                Key: $"entry-{i:D3}",
                Namespace: "prefilter-ns",
                Content: "sharedquerykeyword architecture specification details",
                Metadata: new Dictionary<string, string>
                {
                    ["tenant"] = isTarget ? "acme-corp" : "other-tenant",
                }));
        }

        await vectorStore.SetBatchAsync(entries);
        foreach (var entry in entries)
        {
            retriever.IndexLexicalEntry("prefilter-ns", entry);
        }

        // MaxResults = 1 means candidateFetchCount = 20 (< 55). Without pre-filtering, entry-055 would be cut off!
        var results = await retriever.SearchHybridAsync(
            "prefilter-ns",
            new HybridMemoryQuery(
                QueryText: "sharedquerykeyword architecture",
                MaxResults: 1,
                MetadataFilters: new Dictionary<string, string> { ["tenant"] = "acme-corp" }));

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Entry.Key, Is.EqualTo("entry-055"));
    }

    [Test]
    public void RecursiveCharacterTextChunker_enforces_MaxTokens_budget_using_ITokenCounter()
    {
        var tokenCounter = ApproximateTokenCounter.Instance;
        var chunker = new RecursiveCharacterTextChunker(tokenCounter: tokenCounter);
        const string text =
            "First sentence has several distinct words for token counting. " +
            "Second sentence continues the paragraph with additional technical vocabulary. " +
            "Third sentence concludes the test passage cleanly.";

        var options = new TextChunkingOptions(ChunkSize: 1000, ChunkOverlap: 0, MaxTokens: 12);
        var chunks = chunker.Chunk(text, options);

        Assert.That(chunks, Has.Count.GreaterThanOrEqualTo(3));
        Assert.Multiple(() =>
        {
            foreach (var chunk in chunks)
            {
                Assert.That(tokenCounter.CountTokens(chunk.Content), Is.LessThanOrEqualTo(12));
            }

            Assert.That(string.Concat(chunks.Select(static c => c.Content)), Is.EqualTo(text));
        });
    }

    [Test]
    public async Task RetrievalAugmentationFeature_enforces_MaxContextTokens_with_custom_ITokenCounter()
    {
        var store = new InMemoryMemoryStore();
        var wordTokenCounter = new WordSplittingTokenCounter();

        var services = new ServiceCollection()
            .AddSingleton<IMemoryStore>(store)
            .AddDocumentIngestion(builder => builder.UseTokenCounter(wordTokenCounter))
            .BuildServiceProvider();

        var ingestor = services.GetRequiredService<IDocumentIngestor>();
        await ingestor.IngestAsync(
            "token-budget-kb",
            new IngestDocument(
                Source: "doc://tokens",
                Content: "one two three four five.\n\nsix seven eight nine ten.",
                ChunkingOptions: new TextChunkingOptions(ChunkSize: 30, ChunkOverlap: 0)));

        // Each chunk has 5 word tokens; MaxContextTokens = 6 admits only the first chunk
        var feature = new RetrievalAugmentationFeature(new RetrievalAugmentationOptions(
            Namespace: "token-budget-kb",
            MaxResults: 5,
            MaxContextTokens: 6));

        var assembly = new AgentAssembly();
        feature.Contribute(assembly);
        var pipeline = assembly.Pipeline.Build();

        IReadOnlyList<AgentMessage>? captured = null;
        using var inspectAgent = new InspectingAgent(ctx => captured = ctx.Messages);

        await pipeline.InvokeAsync(
            inspectAgent,
            new AgentTurnContext(
                SessionId: "sess-tok",
                Messages: [new AgentMessage("user", "one two six seven")],
                Services: new AgentTurnServices(services)));

        Assert.That(captured, Is.Not.Null);
        var ragMessage = captured![0].Content;
        Assert.Multiple(() =>
        {
            Assert.That(wordTokenCounter.CallCount, Is.GreaterThan(0));
            Assert.That(ragMessage, Does.Contain("[1] source: doc://tokens"));
            Assert.That(ragMessage, Does.Not.Contain("[2] source: doc://tokens"));
        });
    }

    [Test]
    public async Task VectorMemoryStore_SaveToFileAsync_and_LoadFromFileAsync_preserve_embeddings_without_reembedding()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"myagents-vec-snap-{Guid.NewGuid():N}.json");
        try
        {
            var writerEmbedder = new RecordingEmbeddingGenerator();
            var writerStore = new VectorMemoryStore(writerEmbedder);
            var ingestor = new DefaultDocumentIngestor(writerStore, vectorStore: writerStore);

            await ingestor.IngestAsync(
                "durable-vec",
                new IngestDocument(
                    Source: "doc://persisted",
                    Content: "Persisted vector memory chunk one.\n\nPersisted vector memory chunk two.",
                    Title: "Persisted Guide",
                    ChunkingOptions: new TextChunkingOptions(ChunkSize: 40, ChunkOverlap: 0)));

            await writerStore.SaveToFileAsync(tempFile);
            Assert.That(writerEmbedder.CallCount, Is.EqualTo(1));

            var readerEmbedder = new RecordingEmbeddingGenerator();
            var readerStore = new VectorMemoryStore(readerEmbedder);
            var loadedCount = await readerStore.LoadFromFileAsync(tempFile);

            Assert.Multiple(() =>
            {
                Assert.That(loadedCount, Is.EqualTo(2));
                Assert.That(readerEmbedder.CallCount, Is.Zero, "Loading a snapshot must not invoke the embedding generator.");
            });

            var hits = await readerStore.VectorSearchAsync(
                "durable-vec",
                "Persisted vector memory chunk two",
                maxResults: 2);

            Assert.Multiple(() =>
            {
                Assert.That(hits, Has.Count.EqualTo(2));
                Assert.That(hits[0].Entry.Metadata![DocumentMetadataKeys.DocumentTitle], Is.EqualTo("Persisted Guide"));
            });
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    // ── Slice 6: Reactive Store Notifications, Citation Grounding, Window Expansion & Markdown Chunking ──

    [Test]
    public async Task HybridMemoryRetriever_automatically_syncs_lexical_index_via_IMemoryStoreChangeNotifier_on_direct_Set_Delete_and_Clear()
    {
        var store = new InMemoryMemoryStore();
        using var retriever = new HybridMemoryRetriever(store);

        // Initial seed search on empty namespace
        var initial = await retriever.SearchHybridAsync("reactive-ns", new HybridMemoryQuery("reactivetokenalpha"));
        Assert.That(initial, Is.Empty);

        // 1. Direct store SetAsync without calling IndexLexicalEntry or RefreshLexicalIndexAsync
        await store.SetAsync(new MemoryEntry("k1", "reactive-ns", "reactivetokenalpha distributed consensus protocol"));
        await store.SetAsync(new MemoryEntry("k2", "reactive-ns", "reactivetokenbeta secondary replication log"));

        var afterSet = await retriever.SearchHybridAsync("reactive-ns", new HybridMemoryQuery("reactivetokenalpha"));
        Assert.That(afterSet, Has.Count.EqualTo(1));
        Assert.That(afterSet[0].Entry.Key, Is.EqualTo("k1"));

        // 2. Direct store DeleteAsync without calling RemoveLexicalEntry
        await store.DeleteAsync("reactive-ns", "k1");
        var afterDelete = await retriever.SearchHybridAsync("reactive-ns", new HybridMemoryQuery("reactivetokenalpha"));
        Assert.That(afterDelete, Is.Empty);

        // 3. Direct store ClearAsync without calling RefreshLexicalIndexAsync
        await store.ClearAsync("reactive-ns");
        var afterClear = await retriever.SearchHybridAsync("reactive-ns", new HybridMemoryQuery("reactivetokenbeta"));
        Assert.That(afterClear, Is.Empty);
    }

    [Test]
    public async Task RetrievalAugmentationFeature_evaluates_assistant_citations_and_tags_response_metadata_and_invalid_citations()
    {
        var store = new InMemoryMemoryStore();
        var services = new ServiceCollection()
            .AddSingleton<IMemoryStore>(store)
            .AddDocumentIngestion()
            .BuildServiceProvider();

        var ingestor = services.GetRequiredService<IDocumentIngestor>();
        await ingestor.IngestAsync(
            "grounding-kb",
            new IngestDocument(
                Source: "doc://sla-policy",
                Title: "Enterprise SLA Policy",
                Content: "Tier-1 incidents require a 15-minute response time and 99.99% uptime guarantee."));

        CitationGroundingResult? capturedGrounding = null;
        var feature = new RetrievalAugmentationFeature(new RetrievalAugmentationOptions(
            Namespace: "grounding-kb",
            MaxResults: 3,
            OnCitationsEvaluated: result => capturedGrounding = result,
            AppendCitationReferences: true));

        var assembly = new AgentAssembly();
        feature.Contribute(assembly);
        var pipeline = assembly.Pipeline.Build();

        using var replyingAgent = new InspectingReplyAgent(
            "According to the SLA policy [1], Tier-1 response is 15 minutes (see also [9] for hardware).");

        var turnResult = await pipeline.InvokeAsync(
            replyingAgent,
            new AgentTurnContext(
                SessionId: "sess-ground",
                Messages: [new AgentMessage("user", "Tier-1 response time uptime guarantee")],
                Services: new AgentTurnServices(services)));

        Assert.That(capturedGrounding, Is.Not.Null);
        var assistantMsg = turnResult.Output.Last();

        Assert.Multiple(() =>
        {
            Assert.That(capturedGrounding!.ProvidedContextCount, Is.EqualTo(1));
            Assert.That(capturedGrounding.CitedSources, Has.Count.EqualTo(1));
            Assert.That(capturedGrounding.CitedSources[0].CitationNumber, Is.EqualTo(1));
            Assert.That(capturedGrounding.CitedSources[0].Source, Is.EqualTo("doc://sla-policy"));
            Assert.That(capturedGrounding.CitedSources[0].Title, Is.EqualTo("Enterprise SLA Policy"));
            Assert.That(capturedGrounding.InvalidCitationNumbers, Is.EqualTo(new[] { 9 }));
            Assert.That(capturedGrounding.HasInvalidCitations, Is.True);
            Assert.That(assistantMsg.Content, Does.Contain("References:"));
            Assert.That(assistantMsg.Content, Does.Contain("- [1] Enterprise SLA Policy (doc://sla-policy)"));
        });
    }

    [Test]
    public async Task RetrievalAugmentationFeature_expands_AdjacentChunkWindow_and_deduplicates_neighbor_chunks()
    {
        var store = new InMemoryMemoryStore();
        var services = new ServiceCollection()
            .AddSingleton<IMemoryStore>(store)
            .AddDocumentIngestion()
            .BuildServiceProvider();

        var ingestor = services.GetRequiredService<IDocumentIngestor>();
        await ingestor.IngestAsync(
            "window-kb",
            new IngestDocument(
                Source: "doc://runbook",
                Title: "Failover Runbook",
                Content: "Step 1: Drain incoming traffic.\n\nStep 2: Run uniqueanchorcommand --promote.\n\nStep 3: Verify replica health.",
                ChunkingOptions: new TextChunkingOptions(ChunkSize: 45, ChunkOverlap: 0)));

        var feature = new RetrievalAugmentationFeature(new RetrievalAugmentationOptions(
            Namespace: "window-kb",
            MaxResults: 3,
            AdjacentChunkWindow: 1));

        var assembly = new AgentAssembly();
        feature.Contribute(assembly);
        var pipeline = assembly.Pipeline.Build();

        IReadOnlyList<AgentMessage>? captured = null;
        using var inspectAgent = new InspectingAgent(ctx => captured = ctx.Messages);

        await pipeline.InvokeAsync(
            inspectAgent,
            new AgentTurnContext(
                SessionId: "sess-win",
                Messages: [new AgentMessage("user", "uniqueanchorcommand")],
                Services: new AgentTurnServices(services)));

        Assert.That(captured, Is.Not.Null);
        var ragMessage = captured![0].Content;
        Assert.Multiple(() =>
        {
            // Matching chunk 1 ("Step 2: Run uniqueanchorcommand...") should expand to include chunk 0 and chunk 2
            Assert.That(ragMessage, Does.Contain("Step 1: Drain incoming traffic."));
            Assert.That(ragMessage, Does.Contain("Step 2: Run uniqueanchorcommand --promote."));
            Assert.That(ragMessage, Does.Contain("Step 3: Verify replica health."));
            Assert.That(ragMessage, Does.Not.Contain("[2] source:"));
        });
    }

    [Test]
    public void MarkdownTextChunker_preserves_fenced_code_blocks_and_prepends_heading_breadcrumbs()
    {
        const string markdown = """
            # Operations Guide

            ## Database Failover

            Run the following script to promote the standby node:

            ```bash
            # comment inside code fence must not split as heading
            pg_ctl promote -D /var/lib/postgresql/data
            psql -c "SELECT pg_is_in_recovery();"
            ```

            ## Verification

            Confirm application write traffic succeeds.
            """;

        var chunker = new MarkdownTextChunker(includeHeadingBreadcrumbs: true);
        var chunks = chunker.Chunk(markdown, new TextChunkingOptions(ChunkSize: 220, ChunkOverlap: 20));

        Assert.That(chunks, Has.Count.GreaterThanOrEqualTo(2));
        var codeChunk = chunks.FirstOrDefault(static c => c.Content.Contains("pg_ctl promote", StringComparison.Ordinal));
        var verifyChunk = chunks.FirstOrDefault(static c => c.Content.Contains("Confirm application write traffic", StringComparison.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.That(codeChunk, Is.Not.Null);
            // Entire code fence is preserved intact in a single chunk
            Assert.That(codeChunk!.Content, Does.Contain("```bash"));
            Assert.That(codeChunk.Content, Does.Contain("# comment inside code fence must not split as heading"));
            Assert.That(codeChunk.Content, Does.Contain("SELECT pg_is_in_recovery();"));
            // Breadcrumb includes parent # Operations Guide and ## Database Failover
            Assert.That(codeChunk.Content, Does.Contain("# Operations Guide > ## Database Failover"));

            Assert.That(verifyChunk, Is.Not.Null);
            Assert.That(verifyChunk!.Content, Does.Contain("# Operations Guide > ## Verification"));
        });
    }

    // ── Optimizations: Window Slicing, Token Cutoff, Pre-Normalization, Lexical Deduplication & Zero-Regex Scanning ──

    [Test]
    public void Optimization1_WindowSlicing_splits_efficiently_and_preserves_boundaries()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 50; i++)
        {
            builder.Append($"Section {i:D2}: This is a moderately long sentence describing component architecture {i}. ");
        }

        var fullText = builder.ToString();
        var chunker = new RecursiveCharacterTextChunker();
        var options = new TextChunkingOptions(ChunkSize: 350, ChunkOverlap: 40);
        var chunks = chunker.Chunk(fullText, options);

        Assert.That(chunks, Has.Count.GreaterThan(10));
        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.That(chunks[i].Length, Is.LessThanOrEqualTo(350));
            Assert.That(chunks[i].Content.Length, Is.EqualTo(chunks[i].Length));
            if (i > 0)
            {
                var prevEnd = chunks[i - 1].StartOffset + chunks[i - 1].Length;
                Assert.That(chunks[i].StartOffset, Is.LessThan(prevEnd));
            }
        }
    }

    [Test]
    public void Optimization2_ApproximateTokenCounter_FindMaxLengthForTokenBudget_matches_CountTokens_cutoff()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ApproximateTokenCounter.FindMaxLengthForTokenBudget(ReadOnlySpan<char>.Empty, 10), Is.Zero);
            Assert.That(ApproximateTokenCounter.FindMaxLengthForTokenBudget("hello world".AsSpan(), 0), Is.Zero);
            Assert.That(ApproximateTokenCounter.FindMaxLengthForTokenBudget("hello world".AsSpan(), -5), Is.Zero);
        });

        const string sentence = "The quick brown fox jumps over the lazy dog and explores distributed multi-agent systems.";
        var span = sentence.AsSpan();

        for (var budget = 1; budget <= 20; budget++)
        {
            var maxLen = ApproximateTokenCounter.FindMaxLengthForTokenBudget(span, budget);
            var tokenCount = ApproximateTokenCounter.CountTokens(span[..maxLen]);
            Assert.That(tokenCount, Is.LessThanOrEqualTo(budget), $"Length {maxLen} for budget {budget} exceeded token count.");

            if (maxLen < span.Length)
            {
                Assert.That(maxLen, Is.GreaterThan(0));
            }
        }
    }

    [Test]
    public async Task Optimization3_VectorMemoryStore_pre_normalizes_and_computes_exact_cosine_similarity_via_dot_product()
    {
        var embedder = new ScaledEmbeddingGenerator();
        var store = new VectorMemoryStore(embedder);

        // "unnormalized-doc" produces [3, 4, 0, ...] with norm 5
        await store.SetAsync(new MemoryEntry(
            Key: "doc-vec",
            Namespace: "vec-opt",
            Content: "unnormalized-doc"));

        // "query-vec" produces [6, 8, 0, ...] with norm 10 (same angle as [3, 4])
        var hits = await store.VectorSearchAsync("vec-opt", "query-vec", maxResults: 1);

        Assert.That(hits, Has.Count.EqualTo(1));
        // Both vectors have identical direction; normalized dot product must be 1.0f
        Assert.That(hits[0].Score, Is.EqualTo(1.0f).Within(1e-4f));
    }

    [Test]
    public async Task Optimization4_DefaultDocumentIngestor_autoSynced_avoids_duplicate_BM25_indexing()
    {
        var embedder = new RecordingEmbeddingGenerator();
        var vectorStore = new VectorMemoryStore(embedder);
        var retriever = new HybridMemoryRetriever(vectorStore, vectorStore);
        var ingestor = new DefaultDocumentIngestor(vectorStore, hybridRetriever: retriever, vectorStore: vectorStore);

        var doc = new IngestDocument(
            Source: "doc://reactive-dedup",
            Title: "Reactive Dedup Guide",
            Content: "Alpha uniqueanchorreactive keyword.\n\nBeta uniqueanchorreactive keyword.",
            ChunkingOptions: new TextChunkingOptions(ChunkSize: 45, ChunkOverlap: 0));

        var result = await ingestor.IngestBatchAsync("dedup-ns", [doc]);
        Assert.That(result.TotalChunks, Is.EqualTo(2));

        var hits = await retriever.SearchHybridAsync("dedup-ns", new HybridMemoryQuery("uniqueanchorreactive", MaxResults: 5));
        Assert.That(hits, Has.Count.EqualTo(2));

        var deleted = await ingestor.DeleteDocumentAsync("dedup-ns", "doc://reactive-dedup");
        Assert.That(deleted, Is.EqualTo(2));

        var hitsAfterDelete = await retriever.SearchHybridAsync("dedup-ns", new HybridMemoryQuery("uniqueanchorreactive", MaxResults: 5));
        Assert.That(hitsAfterDelete, Is.Empty);
    }

    [Test]
    public async Task Optimization5_BM25_span_tokenization_and_middleware_overlap_deduplication()
    {
        var index = new BM25Index();
        index.AddOrUpdate("doc1", "Distributed agent runtime supports my-agent_v1.0 and durable checkpoints.");

        var hits = index.Search("my-agent_v1.0", maxResults: 5);
        Assert.That(hits, Has.Count.EqualTo(1));
        Assert.That(hits[0].DocId, Is.EqualTo("doc1"));
        Assert.That(hits[0].Score, Is.GreaterThan(0.0));

        // Test adjacent chunk window deduplication with overlapping chunks
        var store = new InMemoryMemoryStore();
        var services = new ServiceCollection()
            .AddSingleton<IMemoryStore>(store)
            .AddDocumentIngestion()
            .BuildServiceProvider();

        var ingestor = services.GetRequiredService<IDocumentIngestor>();
        await ingestor.IngestAsync(
            "stitch-kb",
            new IngestDocument(
                Source: "doc://stitched",
                Title: "Stitched Guide",
                Content: "Heading A begins here. The quick brown fox jumps over the lazy dog. Heading B ends here.",
                ChunkingOptions: new TextChunkingOptions(ChunkSize: 45, ChunkOverlap: 12)));

        var feature = new RetrievalAugmentationFeature(new RetrievalAugmentationOptions(
            Namespace: "stitch-kb",
            MaxResults: 3,
            AdjacentChunkWindow: 2));

        var assembly = new AgentAssembly();
        feature.Contribute(assembly);
        var pipeline = assembly.Pipeline.Build();

        IReadOnlyList<AgentMessage>? captured = null;
        using var inspectAgent = new InspectingAgent(ctx => captured = ctx.Messages);

        await pipeline.InvokeAsync(
            inspectAgent,
            new AgentTurnContext(
                SessionId: "sess-stitch",
                Messages: [new AgentMessage("user", "quick brown fox")],
                Services: new AgentTurnServices(services)));

        Assert.That(captured, Is.Not.Null);
        var context = captured![0].Content;
        Assert.That(context, Does.Contain("The quick brown fox jumps over the lazy dog."));
    }

    // ── Slice 7: Content-Hash Embedding Deduplication & Durable SqliteMemoryStore ──

    [Test]
    public async Task VectorMemoryStore_reuses_existing_embeddings_for_unchanged_chunks_on_reingest()
    {
        var embedder = new RecordingEmbeddingGenerator();
        var vectorStore = new VectorMemoryStore(embedder);
        var ingestor = new DefaultDocumentIngestor(vectorStore, vectorStore: vectorStore);

        var v1 = new IngestDocument(
            Source: "doc://incremental",
            Title: "Incremental Guide",
            Content: "Paragraph one stays identical.\n\nParagraph two stays identical.\n\nParagraph three original text.",
            ChunkingOptions: new TextChunkingOptions(ChunkSize: 38, ChunkOverlap: 0));

        await ingestor.IngestAsync("inc-ns", v1);
        Assert.Multiple(() =>
        {
            Assert.That(embedder.CallCount, Is.EqualTo(1));
            Assert.That(embedder.Batches[0], Has.Count.EqualTo(3));
        });

        // Re-ingest with 2 unchanged chunks and only the 3rd chunk modified
        var v2 = v1 with
        {
            Content = "Paragraph one stays identical.\n\nParagraph two stays identical.\n\nParagraph three updated text."
        };

        await ingestor.IngestAsync("inc-ns", v2);
        Assert.Multiple(() =>
        {
            Assert.That(embedder.CallCount, Is.EqualTo(2));
            Assert.That(embedder.Batches[1], Has.Count.EqualTo(1), "Only the single modified chunk should be sent to the embedding generator.");
            Assert.That(embedder.Batches[1][0], Is.EqualTo("Paragraph three updated text."));
        });

        // Re-ingest completely identical document -> 0 embedder calls
        await ingestor.IngestAsync("inc-ns", v2);
        Assert.That(embedder.CallCount, Is.EqualTo(2), "Re-ingesting an identical document must make zero embedding calls.");
    }

    [Test]
    public async Task CachingEmbeddingGenerator_deduplicates_within_batch_and_across_repeated_calls_with_eviction()
    {
        var inner = new RecordingEmbeddingGenerator();
        using var caching = new CachingEmbeddingGenerator(inner, maxCacheEntries: 3);

        // Batch with duplicate texts ("alpha", "beta", "alpha") -> only 2 unique inner texts
        var batch1 = await caching.GenerateAsync(["alpha", "beta", "alpha"]);
        Assert.Multiple(() =>
        {
            Assert.That(batch1, Has.Count.EqualTo(3));
            Assert.That(inner.CallCount, Is.EqualTo(1));
            Assert.That(inner.Batches[0], Is.EqualTo(new[] { "alpha", "beta" }));
            Assert.That(caching.CacheHits, Is.Zero);
            Assert.That(caching.CacheMisses, Is.EqualTo(3));
            Assert.That(caching.CachedCount, Is.EqualTo(2));
        });

        // Second call with already-cached texts -> 0 inner calls
        var batch2 = await caching.GenerateAsync(["beta", "alpha"]);
        Assert.Multiple(() =>
        {
            Assert.That(batch2, Has.Count.EqualTo(2));
            Assert.That(inner.CallCount, Is.EqualTo(1), "Cached inputs must not invoke inner generator.");
            Assert.That(caching.CacheHits, Is.EqualTo(2));
        });

        // Exceed maxCacheEntries (3) by adding "gamma" and "delta"
        await caching.GenerateAsync(["gamma", "delta"]);
        Assert.That(caching.CachedCount, Is.EqualTo(3));
    }

    [Test]
    public async Task SqliteMemoryStore_persists_entries_and_embeddings_across_reopen_with_hybrid_search_and_pre_filtering()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"myagents-sqlite-mem-{Guid.NewGuid():N}.db");
        try
        {
            var writerEmbedder = new RecordingEmbeddingGenerator();
            using (var writerStore = new SqliteMemoryStore(dbPath, writerEmbedder))
            {
                var ingestor = new DefaultDocumentIngestor(writerStore, vectorStore: writerStore);
                await ingestor.IngestAsync(
                    "sqlite-kb",
                    new IngestDocument(
                        Source: "doc://sqlite-architecture",
                        Title: "SQLite Architecture",
                        Content: "WAL mode enables concurrent readers.\n\nNormalized float blobs accelerate SIMD dot product.",
                        Metadata: new Dictionary<string, string> { ["tier"] = "storage" },
                        ChunkingOptions: new TextChunkingOptions(ChunkSize: 55, ChunkOverlap: 0)));

                Assert.That(writerEmbedder.CallCount, Is.EqualTo(1));
            }

            // Reopen a brand new SqliteMemoryStore + HybridMemoryRetriever on the same file
            var readerEmbedder = new RecordingEmbeddingGenerator();
            var services = new ServiceCollection()
                .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(readerEmbedder)
                .AddSqliteMemoryStore(dbPath)
                .AddDocumentIngestion()
                .BuildServiceProvider();

            await using (services)
            {
                var sqliteStore = services.GetRequiredService<SqliteMemoryStore>();
                var retriever = services.GetRequiredService<IHybridMemoryRetriever>();

                // Hydrating from disk must not invoke the embedder
                Assert.That(readerEmbedder.CallCount, Is.Zero);

                // Hybrid search (BM25 + dense vector + RRF) works over hydrated SQLite records
                var hits = await retriever.SearchHybridAsync(
                    "sqlite-kb",
                    new HybridMemoryQuery(
                        QueryText: "Normalized float blobs SIMD",
                        MaxResults: 2,
                        MetadataFilters: new Dictionary<string, string> { ["tier"] = "storage" }));

                Assert.Multiple(() =>
                {
                    Assert.That(hits, Is.Not.Empty);
                    Assert.That(hits[0].Entry.Content, Does.Contain("Normalized float blobs"));
                    Assert.That(hits[0].Entry.Metadata![DocumentMetadataKeys.DocumentTitle], Is.EqualTo("SQLite Architecture"));
                });

                // Reactive DeleteAsync updates both SQLite and BM25
                await sqliteStore.DeleteAsync("sqlite-kb", hits[0].Entry.Key);
                var afterDelete = await sqliteStore.GetAsync("sqlite-kb", hits[0].Entry.Key);
                Assert.That(afterDelete, Is.Null);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    // ── Slice 8: VectorDataMemoryStore & Zero-Dependency DocumentReaders ──

    [Test]
    public async Task VectorDataMemoryStore_bridges_VectorStoreCollection_to_hybrid_search_and_reuses_unchanged_embeddings()
    {
        var embedder = new RecordingEmbeddingGenerator();
        using var collection = new FakeVectorStoreCollection("test-collection");

        var services = new ServiceCollection()
            .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embedder)
            .AddVectorDataMemoryStore(collection)
            .AddDocumentIngestion()
            .BuildServiceProvider();

        await using (services)
        {
            var ingestor = services.GetRequiredService<IDocumentIngestor>();
            var retriever = services.GetRequiredService<IHybridMemoryRetriever>();

            var doc = new IngestDocument(
                Source: "doc://vectordata-bridge",
                Title: "VectorData Bridge Spec",
                Content: "First chunk describes VectorStoreCollection integration.\n\nSecond chunk covers vectordataspecialtoken hybrid search.",
                Metadata: new Dictionary<string, string> { ["provider"] = "mevd" },
                ChunkingOptions: new TextChunkingOptions(ChunkSize: 60, ChunkOverlap: 0));

            await ingestor.IngestAsync("vd-ns", doc);
            Assert.That(embedder.CallCount, Is.EqualTo(1));

            // Re-ingesting identical document reuses existing vector from VectorStoreCollection without re-embedding
            await ingestor.IngestAsync("vd-ns", doc);
            Assert.That(embedder.CallCount, Is.EqualTo(1), "Unchanged chunks in VectorDataMemoryStore must not re-invoke embedder.");

            var hits = await retriever.SearchHybridAsync(
                "vd-ns",
                new HybridMemoryQuery(
                    QueryText: "vectordataspecialtoken hybrid",
                    MaxResults: 2,
                    MetadataFilters: new Dictionary<string, string> { ["provider"] = "mevd" }));

            Assert.Multiple(() =>
            {
                Assert.That(hits, Is.Not.Empty);
                Assert.That(hits[0].Entry.Content, Does.Contain("vectordataspecialtoken"));
                Assert.That(hits[0].Entry.Metadata![DocumentMetadataKeys.DocumentTitle], Is.EqualTo("VectorData Bridge Spec"));
            });
        }
    }

    [Test]
    public async Task HtmlDocumentReader_and_CompositeDocumentReader_extract_clean_markdown_text_and_titles_for_IngestStreamAsync()
    {
        const string rawHtml = """
            <!DOCTYPE html>
            <html>
            <head>
                <title>Incident &amp; Failover Playbook</title>
                <style>body { color: red; }</style>
                <script>console.log("ignore script content");</script>
            </head>
            <body>
                <h1>Primary Region Outage</h1>
                <p>When the primary region fails, execute <code>promote-standby</code> immediately.</p>
                <h2>Verification Steps</h2>
                <ul>
                    <li>Check replication lag &lt; 1s</li>
                    <li>Confirm health endpoint returns 200</li>
                </ul>
            </body>
            </html>
            """;

        var store = new InMemoryMemoryStore();
        var services = new ServiceCollection()
            .AddSingleton<IMemoryStore>(store)
            .AddDocumentIngestion(b => b.UseMarkdownChunker(includeHeadingBreadcrumbs: true))
            .BuildServiceProvider();

        var ingestor = services.GetRequiredService<IDocumentIngestor>();
        var retriever = services.GetRequiredService<IHybridMemoryRetriever>();

        await using var htmlStream = new MemoryStream(Encoding.UTF8.GetBytes(rawHtml));
        var ingestionResult = await ingestor.IngestStreamAsync(
            ns: "html-kb",
            content: htmlStream,
            source: "https://runbooks.internal/failover.html",
            contentTypeOrExtension: "text/html",
            chunkingOptions: new TextChunkingOptions(ChunkSize: 300, ChunkOverlap: 0));

        Assert.That(ingestionResult.Chunks, Is.Not.Empty);
        var combinedContent = string.Join("\n", ingestionResult.Chunks.Select(static c => c.Content));

        Assert.Multiple(() =>
        {
            // Extracted <title> and decoded entity &amp;
            Assert.That(ingestionResult.Chunks[0].Metadata![DocumentMetadataKeys.DocumentTitle], Is.EqualTo("Incident & Failover Playbook"));
            // Stripped <script> and <style>
            Assert.That(combinedContent, Does.Not.Contain("console.log"));
            Assert.That(combinedContent, Does.Not.Contain("color: red"));
            // Converted <h1>, <h2>, and <li> to Markdown structure & decoded &lt;
            Assert.That(combinedContent, Does.Contain("# Primary Region Outage"));
            Assert.That(combinedContent, Does.Contain("## Verification Steps"));
            Assert.That(combinedContent, Does.Contain("- Check replication lag < 1s"));
        });

        var searchHits = await retriever.SearchHybridAsync("html-kb", new HybridMemoryQuery("promote-standby replication lag"));
        Assert.That(searchHits, Is.Not.Empty);
    }

    // ── Test Doubles ──────────────────────────────────────────────────────────

    private sealed class InspectingReplyAgent(string replyText) : IAgent
    {
        public ValueTask<AgentTurnResult> InvokeAsync(AgentTurnContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentTurnResult(
                [new AgentMessage("assistant", replyText)],
                new AgentTurnTelemetry(TimeSpan.Zero)));

        public async IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
            AgentTurnContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return await InvokeAsync(context, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose() { }
    }

    private sealed class RecordingTextChunker : ITextChunker
    {
        public int InvocationCount { get; private set; }
        public string? LastInput { get; private set; }

        public IReadOnlyList<TextChunk> Chunk(string text, TextChunkingOptions? options = null)
        {
            InvocationCount++;
            LastInput = text;
            var transformed = $"[custom-chunk] {text}";
            return [new TextChunk(0, transformed, 0, transformed.Length)];
        }
    }

    private sealed class RecordingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int CallCount { get; private set; }
        public List<IReadOnlyList<string>> Batches { get; } = [];
        public Exception? FailureToThrow { get; set; }

        public EmbeddingGeneratorMetadata Metadata { get; } = new("RecordingEmbedder");

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var batch = values.ToArray();
            Batches.Add(batch);

            if (FailureToThrow is not null)
            {
                throw FailureToThrow;
            }

            var embeddings = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var text in batch)
            {
                embeddings.Add(new Embedding<float>(CreateDeterministicVector(text)));
            }

            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        private static float[] CreateDeterministicVector(string text)
        {
            var vector = new float[8];
            vector[0] = 1.0f;
            foreach (var ch in text)
            {
                vector[(ch % 7) + 1] += 0.1f;
            }
            return vector;
        }
    }

    private sealed class CountingMemoryStore : IMemoryStore
    {
        private readonly InMemoryMemoryStore _inner = new();
        public int SearchCallCount { get; private set; }

        public ValueTask SetAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
            => _inner.SetAsync(entry, cancellationToken);

        public ValueTask<MemoryEntry?> GetAsync(string ns, string key, CancellationToken cancellationToken = default)
            => _inner.GetAsync(ns, key, cancellationToken);

        public ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(
            string ns,
            string? query = null,
            int maxResults = 10,
            CancellationToken cancellationToken = default)
        {
            SearchCallCount++;
            return _inner.SearchAsync(ns, query, maxResults, cancellationToken);
        }

        public ValueTask DeleteAsync(string ns, string key, CancellationToken cancellationToken = default)
            => _inner.DeleteAsync(ns, key, cancellationToken);

        public ValueTask ClearAsync(string ns, CancellationToken cancellationToken = default)
            => _inner.ClearAsync(ns, cancellationToken);
    }

    private sealed class InspectingAgent(Action<AgentTurnContext> onTurn) : IAgent
    {
        public ValueTask<AgentTurnResult> InvokeAsync(AgentTurnContext context, CancellationToken cancellationToken = default)
        {
            onTurn(context);
            return ValueTask.FromResult(new AgentTurnResult(
                [new AgentMessage("assistant", "Done")],
                new AgentTurnTelemetry(TimeSpan.Zero)));
        }

        public async IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
            AgentTurnContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return await InvokeAsync(context, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose() { }
    }

    private sealed class StubSummaryGenerator(string summaryText) : IMemorySummaryGenerator
    {
        public ValueTask<string> GenerateAsync(
            string ns,
            IReadOnlyList<MemoryEntry> entries,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(summaryText);
    }

    private sealed class WordSplittingTokenCounter : ITokenCounter
    {
        public int CallCount { get; private set; }

        public int CountTokens(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            CallCount++;
            return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        }
    }

    private sealed class ScaledEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public EmbeddingGeneratorMetadata Metadata { get; } = new("ScaledEmbedder");

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var embeddings = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var text in values)
            {
                var vector = new float[8];
                if (text.Contains("query", StringComparison.OrdinalIgnoreCase))
                {
                    vector[0] = 6.0f;
                    vector[1] = 8.0f;
                }
                else
                {
                    vector[0] = 3.0f;
                    vector[1] = 4.0f;
                }
                embeddings.Add(new Embedding<float>(vector));
            }

            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FakeVectorStoreCollection(string name)
        : VectorStoreCollection<string, VectorDataMemoryRecord>
    {
        private readonly Dictionary<string, VectorDataMemoryRecord> _records = new(StringComparer.Ordinal);

        public override string Name => name;

        public override Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public override Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public override Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default)
        {
            _records.Clear();
            return Task.CompletedTask;
        }

        public override Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            _records.Remove(key);
            return Task.CompletedTask;
        }

        public override Task DeleteAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
        {
            foreach (var key in keys)
            {
                _records.Remove(key);
            }
            return Task.CompletedTask;
        }

        public override Task<VectorDataMemoryRecord?> GetAsync(
            string key,
            RecordRetrievalOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _records.TryGetValue(key, out var record);
            return Task.FromResult(record);
        }

        public override async IAsyncEnumerable<VectorDataMemoryRecord> GetAsync(
            IEnumerable<string> keys,
            RecordRetrievalOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            foreach (var key in keys)
            {
                if (_records.TryGetValue(key, out var rec))
                {
                    yield return rec;
                }
            }
        }

        public override async IAsyncEnumerable<VectorDataMemoryRecord> GetAsync(
            System.Linq.Expressions.Expression<Func<VectorDataMemoryRecord, bool>> filter,
            int top,
            FilteredRecordRetrievalOptions<VectorDataMemoryRecord>? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            var predicate = filter.Compile();
            var count = 0;
            foreach (var rec in _records.Values)
            {
                if (predicate(rec))
                {
                    yield return rec;
                    if (++count >= top)
                    {
                        yield break;
                    }
                }
            }
        }

        public override Task UpsertAsync(VectorDataMemoryRecord record, CancellationToken cancellationToken = default)
        {
            _records[record.Id] = record;
            return Task.CompletedTask;
        }

        public override Task UpsertAsync(IEnumerable<VectorDataMemoryRecord> records, CancellationToken cancellationToken = default)
        {
            foreach (var rec in records)
            {
                _records[rec.Id] = rec;
            }
            return Task.CompletedTask;
        }

        public override async IAsyncEnumerable<VectorSearchResult<VectorDataMemoryRecord>> SearchAsync<TInput>(
            TInput searchValue,
            int top,
            VectorSearchOptions<VectorDataMemoryRecord>? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            if (searchValue is not ReadOnlyMemory<float> queryVec)
            {
                yield break;
            }

            var predicate = options?.Filter?.Compile();
            var scored = new List<(VectorDataMemoryRecord Record, double Score)>();
            foreach (var rec in _records.Values)
            {
                if (predicate is not null && !predicate(rec))
                {
                    continue;
                }

                var sim = TensorPrimitives.Dot(queryVec.Span, rec.Embedding.Span);
                scored.Add((rec, sim));
            }

            foreach (var item in scored.OrderByDescending(static x => x.Score).Take(top))
            {
                yield return new VectorSearchResult<VectorDataMemoryRecord>(item.Record, item.Score);
            }
        }

        public override object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
