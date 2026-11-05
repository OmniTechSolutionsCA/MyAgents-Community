using System.Globalization;
using Microsoft.Extensions.AI;
using MyAgents.Core.Memory;

namespace MyAgents.Memory;

/// <summary>
/// Default implementation of <see cref="IDocumentIngestor"/> that chunks documents via <see cref="ITextChunker"/>,
/// performs single-batch embedding generation, replaces prior chunks for the same source URI, and synchronizes
/// the <see cref="IHybridMemoryRetriever"/> lexical index.
/// </summary>
public sealed class DefaultDocumentIngestor : IDocumentIngestor
{
    private readonly IMemoryStore _store;
    private readonly ITextChunker _chunker;
    private readonly IHybridMemoryRetriever? _hybridRetriever;
    private readonly IVectorMemoryStore? _vectorStore;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embedder;
    private readonly TextChunkingOptions? _defaultChunkingOptions;

    /// <summary>
    /// Initializes a new instance of <see cref="DefaultDocumentIngestor"/>.
    /// </summary>
    public DefaultDocumentIngestor(
        IMemoryStore store,
        ITextChunker? chunker = null,
        IHybridMemoryRetriever? hybridRetriever = null,
        IVectorMemoryStore? vectorStore = null,
        IEmbeddingGenerator<string, Embedding<float>>? embedder = null,
        TextChunkingOptions? defaultChunkingOptions = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _chunker = chunker ?? new RecursiveCharacterTextChunker(defaultChunkingOptions);
        _hybridRetriever = hybridRetriever;
        _vectorStore = vectorStore ?? store as IVectorMemoryStore;
        _embedder = embedder;
        _defaultChunkingOptions = defaultChunkingOptions;
    }

    /// <inheritdoc />
    public async ValueTask<DocumentIngestionResult> IngestAsync(
        string ns,
        IngestDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentNullException.ThrowIfNull(document);

        var batchResult = await IngestBatchAsync(ns, [document], cancellationToken).ConfigureAwait(false);
        return batchResult.Documents[0];
    }

    /// <inheritdoc />
    public async ValueTask<BatchDocumentIngestionResult> IngestBatchAsync(
        string ns,
        IReadOnlyList<IngestDocument> documents,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentNullException.ThrowIfNull(documents);

        if (documents.Count == 0)
        {
            return new BatchDocumentIngestionResult(ns, [], 0, 0);
        }

        for (var d = 0; d < documents.Count; d++)
        {
            var doc = documents[d];
            ArgumentNullException.ThrowIfNull(doc);
            ArgumentException.ThrowIfNullOrWhiteSpace(doc.Source);
            ArgumentNullException.ThrowIfNull(doc.Content);
        }

        var now = DateTimeOffset.UtcNow;
        var perDocChunks = new MemoryEntry[documents.Count][];
        var allNewEntries = new List<MemoryEntry>();
        var newKeysBySource = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        for (var d = 0; d < documents.Count; d++)
        {
            var document = documents[d];
            var chunkingOptions = document.ChunkingOptions ?? _defaultChunkingOptions;
            var textChunks = _chunker.Chunk(document.Content, chunkingOptions);
            var title = string.IsNullOrWhiteSpace(document.Title) ? document.Source : document.Title;
            var chunkCountText = textChunks.Count.ToString(CultureInfo.InvariantCulture);

            var docEntries = new MemoryEntry[textChunks.Count];
            if (!newKeysBySource.TryGetValue(document.Source, out var sourceKeySet))
            {
                sourceKeySet = new HashSet<string>(StringComparer.Ordinal);
                newKeysBySource[document.Source] = sourceKeySet;
            }
            else
            {
                sourceKeySet.Clear();
            }

            for (var i = 0; i < textChunks.Count; i++)
            {
                var chunk = textChunks[i];
                var key = $"{document.Source}#chunk-{i:D4}";
                sourceKeySet.Add(key);

                var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (document.Metadata is not null)
                {
                    foreach (var (k, v) in document.Metadata)
                    {
                        metadata[k] = v;
                    }
                }

                metadata[DocumentMetadataKeys.Source] = document.Source;
                metadata[DocumentMetadataKeys.DocumentTitle] = title;
                metadata[DocumentMetadataKeys.ChunkIndex] = i.ToString(CultureInfo.InvariantCulture);
                metadata[DocumentMetadataKeys.ChunkCount] = chunkCountText;

                var entry = new MemoryEntry(
                    Key: key,
                    Namespace: ns,
                    Content: chunk.Content,
                    Metadata: metadata,
                    CreatedAt: now);

                docEntries[i] = entry;
                allNewEntries.Add(entry);
            }

            perDocChunks[d] = docEntries;
        }

        // Locate any previously ingested chunks for the target sources before mutating the store
        var existingNamespaceEntries = await _store.SearchAsync(
            ns,
            maxResults: int.MaxValue,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var previousBySource = new Dictionary<string, List<MemoryEntry>>(StringComparer.Ordinal);
        for (var i = 0; i < existingNamespaceEntries.Count; i++)
        {
            var existing = existingNamespaceEntries[i];
            if (existing.Metadata is not null &&
                existing.Metadata.TryGetValue(DocumentMetadataKeys.Source, out var existingSource) &&
                !string.IsNullOrWhiteSpace(existingSource) &&
                newKeysBySource.ContainsKey(existingSource))
            {
                if (!previousBySource.TryGetValue(existingSource, out var list))
                {
                    list = [];
                    previousBySource[existingSource] = list;
                }
                list.Add(existing);
            }
        }

        var previousByKey = new Dictionary<string, MemoryEntry>(StringComparer.Ordinal);
        foreach (var (_, oldEntries) in previousBySource)
        {
            for (var i = 0; i < oldEntries.Count; i++)
            {
                var old = oldEntries[i];
                previousByKey[old.Key] = old;
            }
        }

        // Perform single-batch embedding and persistence first so an embedder failure leaves prior state intact
        if (allNewEntries.Count > 0)
        {
            if (_vectorStore is not null)
            {
                await _vectorStore.SetBatchAsync(allNewEntries, cancellationToken).ConfigureAwait(false);
                if (!ReferenceEquals(_store, _vectorStore))
                {
                    for (var i = 0; i < allNewEntries.Count; i++)
                    {
                        await _store.SetAsync(allNewEntries[i], cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                if (_embedder is not null)
                {
                    var changedTexts = new List<string>(allNewEntries.Count);
                    for (var i = 0; i < allNewEntries.Count; i++)
                    {
                        var candidate = allNewEntries[i];
                        if (!previousByKey.TryGetValue(candidate.Key, out var prior) ||
                            !string.Equals(prior.Content, candidate.Content, StringComparison.Ordinal))
                        {
                            changedTexts.Add(candidate.Content);
                        }
                    }

                    if (changedTexts.Count > 0)
                    {
                        var generated = await _embedder.GenerateAsync(changedTexts, cancellationToken: cancellationToken).ConfigureAwait(false);
                        if (generated.Count != changedTexts.Count)
                        {
                            throw new InvalidOperationException(
                                $"Embedding generator returned {generated.Count} embeddings for a batch of {changedTexts.Count} chunks.");
                        }
                    }
                }

                for (var i = 0; i < allNewEntries.Count; i++)
                {
                    await _store.SetAsync(allNewEntries[i], cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var autoSynced = _store is IMemoryStoreChangeNotifier;

        // Remove stale chunks from previous versions of the ingested sources
        foreach (var (source, oldEntries) in previousBySource)
        {
            var activeKeys = newKeysBySource[source];
            for (var i = 0; i < oldEntries.Count; i++)
            {
                var oldEntry = oldEntries[i];
                if (!activeKeys.Contains(oldEntry.Key))
                {
                    await _store.DeleteAsync(ns, oldEntry.Key, cancellationToken).ConfigureAwait(false);
                    if (_vectorStore is not null && !ReferenceEquals(_store, _vectorStore))
                    {
                        await _vectorStore.DeleteAsync(ns, oldEntry.Key, cancellationToken).ConfigureAwait(false);
                    }

                    if (!autoSynced)
                    {
                        _hybridRetriever?.RemoveLexicalEntry(ns, oldEntry.Key);
                    }
                }
            }
        }

        // Synchronize BM25 lexical index for all new chunks if not automatically handled by change notifier
        if (!autoSynced && _hybridRetriever is not null)
        {
            for (var i = 0; i < allNewEntries.Count; i++)
            {
                _hybridRetriever.IndexLexicalEntry(ns, allNewEntries[i]);
            }
        }

        var docResults = new DocumentIngestionResult[documents.Count];
        var totalReplaced = 0;
        for (var d = 0; d < documents.Count; d++)
        {
            var source = documents[d].Source;
            var replacedCount = previousBySource.TryGetValue(source, out var oldList) ? oldList.Count : 0;
            totalReplaced += replacedCount;
            docResults[d] = new DocumentIngestionResult(
                Namespace: ns,
                Source: source,
                Chunks: perDocChunks[d],
                ReplacedChunkCount: replacedCount);
        }

        return new BatchDocumentIngestionResult(
            Namespace: ns,
            Documents: docResults,
            TotalChunks: allNewEntries.Count,
            TotalReplacedChunks: totalReplaced);
    }

    /// <inheritdoc />
    public async ValueTask<int> DeleteDocumentAsync(
        string ns,
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var existingEntries = await _store.SearchAsync(
            ns,
            maxResults: int.MaxValue,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var autoSynced = _store is IMemoryStoreChangeNotifier;
        var deletedCount = 0;
        for (var i = 0; i < existingEntries.Count; i++)
        {
            var entry = existingEntries[i];
            if (entry.Metadata is not null &&
                entry.Metadata.TryGetValue(DocumentMetadataKeys.Source, out var entrySource) &&
                string.Equals(entrySource, source, StringComparison.Ordinal))
            {
                await _store.DeleteAsync(ns, entry.Key, cancellationToken).ConfigureAwait(false);
                if (_vectorStore is not null && !ReferenceEquals(_store, _vectorStore))
                {
                    await _vectorStore.DeleteAsync(ns, entry.Key, cancellationToken).ConfigureAwait(false);
                }

                if (!autoSynced)
                {
                    _hybridRetriever?.RemoveLexicalEntry(ns, entry.Key);
                }
                deletedCount++;
            }
        }

        return deletedCount;
    }
}
