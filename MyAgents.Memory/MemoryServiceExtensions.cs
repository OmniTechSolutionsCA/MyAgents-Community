using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyAgents.Core.Memory;

namespace MyAgents.Memory;

/// <summary>
/// DI helpers for ranked retrieval, hybrid BM25/vector search, episodic memory, and retention services.
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
                sp.GetService<IMemorySummaryGenerator>()));
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

    private sealed class NullMemoryTelemetrySink : IMemoryTelemetrySink
    {
        public ValueTask RecordAsync(MemoryRetrievalTelemetry telemetry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}