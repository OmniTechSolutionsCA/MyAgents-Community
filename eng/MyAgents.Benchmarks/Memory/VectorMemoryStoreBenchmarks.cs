using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using MyAgents.Core.Memory;
using MyAgents.Memory;

namespace MyAgents.Benchmarks.Memory;

[MemoryDiagnoser]
public class VectorMemoryStoreBenchmarks
{
    private sealed class DeterministicEmbeddingGenerator(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly Embedding<float> _cachedEmbedding = new(CreateNormalizedVector(dimensions));

        private static float[] CreateNormalizedVector(int dim)
        {
            var vec = new float[dim];
            var val = 1.0f / MathF.Sqrt(dim);
            Array.Fill(vec, val);
            return vec;
        }

        public EmbeddingGeneratorMetadata Metadata => new("Deterministic");

        public void Dispose() { }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var count = values is ICollection<string> col ? col.Count : values.Count();
            var list = new GeneratedEmbeddings<Embedding<float>>();
            for (var i = 0; i < count; i++)
            {
                list.Add(_cachedEmbedding);
            }
            return Task.FromResult(list);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private VectorMemoryStore _store = null!;
    private readonly MemoryEntry _upsertEntry = new(
        Key: "bench_entry_new",
        Namespace: "bench_ns",
        Content: "High performance memory entry for benchmarking upsert operations",
        Metadata: new Dictionary<string, string> { ["env"] = "prod", ["tier"] = "hot" });

    [GlobalSetup]
    public void Setup()
    {
        var embedder = new DeterministicEmbeddingGenerator(1536);
        _store = new VectorMemoryStore(embedder);

        for (var i = 0; i < 5000; i++)
        {
            _store.SetAsync(new MemoryEntry(
                Key: $"key_{i}",
                Namespace: "bench_ns",
                Content: $"Stored document content number {i} with semantic information",
                Metadata: new Dictionary<string, string> { ["id"] = i.ToString(), ["category"] = "benchmark" })).AsTask().GetAwaiter().GetResult();
        }
    }

    [Benchmark]
    public async ValueTask<IReadOnlyList<VectorSearchResult>> VectorSearch_Top10_1536Dim()
    {
        return await _store.VectorSearchAsync("bench_ns", "semantic information query", maxResults: 10).ConfigureAwait(false);
    }

    [Benchmark]
    public async ValueTask SetAsync_UpsertWithMetadata()
    {
        await _store.SetAsync(_upsertEntry).ConfigureAwait(false);
    }
}
