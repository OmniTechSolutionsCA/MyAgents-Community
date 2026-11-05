using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.VectorData;
using MyAgents.Core;
using MyAgents.Core.Memory;

namespace MyAgents.Memory;

/// <summary>
/// Builder for configuring document chunking, token counting, and ingestion services.
/// </summary>
public sealed class DocumentIngestionBuilder
{
    private ITextChunker? _chunker;
    private ITokenCounter? _tokenCounter;
    private TextChunkingOptions? _chunkingOptions;
    private bool _useMarkdownChunker;
    private bool _includeHeadingBreadcrumbs = true;

    /// <summary>
    /// Underlying service collection when used with dependency injection.
    /// </summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Creates a new <see cref="DocumentIngestionBuilder"/>.
    /// </summary>
    public DocumentIngestionBuilder(IServiceCollection? services = null)
    {
        Services = services ?? new ServiceCollection();
    }

    /// <summary>
    /// Registers a custom <see cref="ITextChunker"/> instance for document ingestion.
    /// </summary>
    public DocumentIngestionBuilder UseChunker(ITextChunker chunker)
    {
        ArgumentNullException.ThrowIfNull(chunker);
        _chunker = chunker;
        _useMarkdownChunker = false;
        Services.Replace(ServiceDescriptor.Singleton(chunker));
        return this;
    }

    /// <summary>
    /// Registers a custom <see cref="ITextChunker"/> type for document ingestion.
    /// </summary>
    public DocumentIngestionBuilder UseChunker<TChunker>()
        where TChunker : class, ITextChunker
    {
        _chunker = null;
        _useMarkdownChunker = false;
        Services.Replace(ServiceDescriptor.Singleton<ITextChunker, TChunker>());
        return this;
    }

    /// <summary>
    /// Configures <see cref="MarkdownTextChunker"/> for heading-aware and code-fence-preserving document chunking.
    /// </summary>
    public DocumentIngestionBuilder UseMarkdownChunker(bool includeHeadingBreadcrumbs = true)
    {
        _chunker = null;
        _useMarkdownChunker = true;
        _includeHeadingBreadcrumbs = includeHeadingBreadcrumbs;
        Services.Replace(ServiceDescriptor.Singleton<ITextChunker>(sp =>
            new MarkdownTextChunker(
                sp.GetService<TextChunkingOptions>(),
                sp.GetService<ITokenCounter>(),
                includeHeadingBreadcrumbs)));
        return this;
    }

    /// <summary>
    /// Registers a custom <see cref="ITokenCounter"/> instance for token-aware chunking and context budgets.
    /// </summary>
    public DocumentIngestionBuilder UseTokenCounter(ITokenCounter tokenCounter)
    {
        ArgumentNullException.ThrowIfNull(tokenCounter);
        _tokenCounter = tokenCounter;
        Services.Replace(ServiceDescriptor.Singleton(tokenCounter));
        return this;
    }

    /// <summary>
    /// Registers a custom <see cref="ITokenCounter"/> type for token-aware chunking and context budgets.
    /// </summary>
    public DocumentIngestionBuilder UseTokenCounter<TTokenCounter>()
        where TTokenCounter : class, ITokenCounter
    {
        _tokenCounter = null;
        Services.Replace(ServiceDescriptor.Singleton<ITokenCounter, TTokenCounter>());
        return this;
    }

    /// <summary>
    /// Configures default <see cref="TextChunkingOptions"/> for chunking during ingestion.
    /// </summary>
    public DocumentIngestionBuilder WithChunkingOptions(TextChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _chunkingOptions = options;
        Services.Replace(ServiceDescriptor.Singleton(options));
        return this;
    }

    /// <summary>
    /// Registers a custom <see cref="IDocumentReader"/> instance for document format extraction.
    /// </summary>
    public DocumentIngestionBuilder UseDocumentReader(IDocumentReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        Services.Replace(ServiceDescriptor.Singleton(reader));
        return this;
    }

    /// <summary>
    /// Registers a custom <see cref="IDocumentReader"/> type for document format extraction.
    /// </summary>
    public DocumentIngestionBuilder UseDocumentReader<TReader>()
        where TReader : class, IDocumentReader
    {
        Services.Replace(ServiceDescriptor.Singleton<IDocumentReader, TReader>());
        return this;
    }

    /// <summary>
    /// Builds an <see cref="IDocumentIngestor"/> directly using the configured chunker, token counter, and options.
    /// </summary>
    public IDocumentIngestor Build(
        IMemoryStore store,
        IHybridMemoryRetriever? hybridRetriever = null,
        IVectorMemoryStore? vectorStore = null,
        IEmbeddingGenerator<string, Embedding<float>>? embedder = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var chunker = _chunker ?? (_useMarkdownChunker
            ? new MarkdownTextChunker(_chunkingOptions, _tokenCounter, _includeHeadingBreadcrumbs)
            : new RecursiveCharacterTextChunker(_chunkingOptions, _tokenCounter));
        return new DefaultDocumentIngestor(
            store: store,
            chunker: chunker,
            hybridRetriever: hybridRetriever,
            vectorStore: vectorStore,
            embedder: embedder,
            defaultChunkingOptions: _chunkingOptions);
    }
}

/// <summary>
/// DI helpers for ranked retrieval, hybrid BM25/vector search, episodic memory, document ingestion, and retention services.
/// </summary>
public static class MemoryServiceExtensions
{
    public static IServiceCollection AddMemoryRetrieval(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IMemoryRetriever>(sp =>
        {
            var store = sp.GetRequiredService<IMemoryStore>();
            var vector = store as IVectorMemoryStore ?? sp.GetService<IVectorMemoryStore>();
            var telemetrySink = sp.GetService<IMemoryTelemetrySink>();
            return new DefaultMemoryRetriever(store, vector, telemetrySink);
        });
        services.TryAddSingleton<IMemoryTelemetrySink, NullMemoryTelemetrySink>();
        services.TryAddSingleton<MemoryRetentionService>(sp =>
            new MemoryRetentionService(
                sp.GetRequiredService<IMemoryStore>(),
                sp.GetService<IMemorySummaryGenerator>(),
                sp.GetService<IHybridMemoryRetriever>()));
        return services;
    }

    /// <summary>
    /// Registers the hybrid BM25 and vector retriever combining reciprocal rank fusion.
    /// </summary>
    public static IServiceCollection AddHybridMemoryRetrieval(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IHybridMemoryRetriever>(sp =>
        {
            var store = sp.GetRequiredService<IMemoryStore>();
            var vector = store as IVectorMemoryStore ?? sp.GetService<IVectorMemoryStore>();
            var telemetrySink = sp.GetService<IMemoryTelemetrySink>();
            return new HybridMemoryRetriever(store, vector, telemetrySink);
        });
        services.TryAddSingleton<IMemoryRetriever>(sp => sp.GetRequiredService<IHybridMemoryRetriever>());
        services.TryAddSingleton<IMemoryTelemetrySink, NullMemoryTelemetrySink>();
        services.TryAddSingleton<MemoryRetentionService>(sp =>
            new MemoryRetentionService(
                sp.GetRequiredService<IMemoryStore>(),
                sp.GetService<IMemorySummaryGenerator>(),
                sp.GetService<IHybridMemoryRetriever>()));

        return services;
    }

    /// <summary>
    /// Registers document chunking, ingestion services, hybrid memory retrieval, and knowledge base agent tools.
    /// </summary>
    public static IServiceCollection AddDocumentIngestion(
        this IServiceCollection services,
        Action<DocumentIngestionBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHybridMemoryRetrieval();

        var builder = new DocumentIngestionBuilder(services);
        configure?.Invoke(builder);

        services.TryAddSingleton<ITokenCounter>(ApproximateTokenCounter.Instance);
        services.TryAddSingleton<IDocumentReader>(static _ => new CompositeDocumentReader());

        services.TryAddSingleton<ITextChunker>(sp =>
            new RecursiveCharacterTextChunker(
                sp.GetService<TextChunkingOptions>(),
                sp.GetService<ITokenCounter>()));

        services.TryAddSingleton<IDocumentIngestor>(sp =>
        {
            var store = sp.GetRequiredService<IMemoryStore>();
            var chunker = sp.GetRequiredService<ITextChunker>();
            var hybridRetriever = sp.GetService<IHybridMemoryRetriever>();
            var vectorStore = store as IVectorMemoryStore ?? sp.GetService<IVectorMemoryStore>();
            var embedder = sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
            var defaultOptions = sp.GetService<TextChunkingOptions>();
            return new DefaultDocumentIngestor(
                store: store,
                chunker: chunker,
                hybridRetriever: hybridRetriever,
                vectorStore: vectorStore,
                embedder: embedder,
                defaultChunkingOptions: defaultOptions);
        });

        services.TryAddTransient<KnowledgeBaseFeature>(sp =>
            new KnowledgeBaseFeature(
                sp.GetRequiredService<IHybridMemoryRetriever>(),
                sp.GetService<IDocumentIngestor>()));

        return services;
    }

    /// <summary>
    /// Registers long-term episodic memory store and tool feature.
    /// </summary>
    public static IServiceCollection AddEpisodicMemory(this IServiceCollection services, string ns = EpisodicMemoryStore.DefaultNamespace)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHybridMemoryRetrieval();
        services.TryAddSingleton<IEpisodicMemoryStore>(sp =>
        {
            var store = sp.GetRequiredService<IMemoryStore>();
            var retriever = sp.GetRequiredService<IHybridMemoryRetriever>();
            return new EpisodicMemoryStore(store, retriever, ns);
        });
        services.TryAddTransient<EpisodicMemoryFeature>(sp =>
        {
            var episodicStore = sp.GetRequiredService<IEpisodicMemoryStore>();
            return new EpisodicMemoryFeature(episodicStore);
        });

        return services;
    }

    /// <summary>
    /// Registers a durable SQLite-backed <see cref="SqliteMemoryStore"/> as both <see cref="IMemoryStore"/>
    /// and <see cref="IVectorMemoryStore"/>, along with hybrid memory retrieval.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionStringOrFilePath">SQLite database file path or connection string.</param>
    public static IServiceCollection AddSqliteMemoryStore(
        this IServiceCollection services,
        string connectionStringOrFilePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringOrFilePath);

        services.TryAddSingleton<SqliteMemoryStore>(sp =>
            new SqliteMemoryStore(
                connectionStringOrFilePath,
                sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>()));
        services.TryAddSingleton<IMemoryStore>(sp => sp.GetRequiredService<SqliteMemoryStore>());
        services.TryAddSingleton<IVectorMemoryStore>(sp => sp.GetRequiredService<SqliteMemoryStore>());
        services.AddHybridMemoryRetrieval();

        return services;
    }

    /// <summary>
    /// Registers a <see cref="VectorDataMemoryStore"/> backed by a <see cref="VectorStoreCollection{TKey, TRecord}"/>
    /// as both <see cref="IMemoryStore"/> and <see cref="IVectorMemoryStore"/>, along with hybrid memory retrieval.
    /// </summary>
    public static IServiceCollection AddVectorDataMemoryStore(
        this IServiceCollection services,
        VectorStoreCollection<string, VectorDataMemoryRecord>? collection = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<VectorDataMemoryStore>(sp =>
        {
            var resolvedCollection = collection ?? sp.GetRequiredService<VectorStoreCollection<string, VectorDataMemoryRecord>>();
            var embedder = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
            return new VectorDataMemoryStore(resolvedCollection, embedder);
        });
        services.TryAddSingleton<IMemoryStore>(sp => sp.GetRequiredService<VectorDataMemoryStore>());
        services.TryAddSingleton<IVectorMemoryStore>(sp => sp.GetRequiredService<VectorDataMemoryStore>());
        services.AddHybridMemoryRetrieval();

        return services;
    }

    private sealed class NullMemoryTelemetrySink : IMemoryTelemetrySink
    {
        public ValueTask RecordAsync(MemoryRetrievalTelemetry telemetry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
