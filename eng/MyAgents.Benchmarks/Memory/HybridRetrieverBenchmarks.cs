using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using MyAgents.Core.Memory;
using MyAgents.Memory;

namespace MyAgents.Benchmarks.Memory;

[MemoryDiagnoser]
public class HybridRetrieverBenchmarks
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

    private HybridMemoryRetriever _retriever = null!;
    private readonly HybridMemoryQuery _rrfQuery = new(
        QueryText: "distributed consensus raft leader election",
        MaxResults: 10,
        RankingMode: HybridRankingMode.ReciprocalRankFusion);

    private readonly HybridMemoryQuery _linearQuery = new(
        QueryText: "distributed consensus raft leader election",
        MaxResults: 10,
        RankingMode: HybridRankingMode.WeightedLinearCombination);

    [GlobalSetup]
    public void Setup()
    {
        var embedder = new DeterministicEmbeddingGenerator(384);
        var vectorStore = new VectorMemoryStore(embedder);
        var inMemoryStore = new InMemoryMemoryStore();
        _retriever = new HybridMemoryRetriever(inMemoryStore, vectorStore);

        var vocabulary = new[]
        {
            "distributed", "systems", "consensus", "algorithm", "raft", "paxos", "state", "machine",
            "replication", "leader", "election", "log", "entries", "heartbeat", "timeout", "cluster"
        };

        var rng = new Random(42);
        for (var i = 0; i < 5000; i++)
        {
            var wordCount = rng.Next(10, 20);
            var words = new string[wordCount];
            for (var w = 0; w < wordCount; w++)
            {
                words[w] = vocabulary[rng.Next(vocabulary.Length)];
            }
            var content = string.Join(" ", words);
            var entry = new MemoryEntry($"key_{i}", "bench_ns", content);
            inMemoryStore.SetAsync(entry).AsTask().GetAwaiter().GetResult();
            vectorStore.SetAsync(entry).AsTask().GetAwaiter().GetResult();
            _retriever.IndexLexicalEntry("bench_ns", entry);
        }
    }

    [Benchmark]
    public async ValueTask<IReadOnlyList<HybridMemorySearchResult>> Retrieve_ReciprocalRankFusion_Top10()
    {
        return await _retriever.SearchHybridAsync("bench_ns", _rrfQuery).ConfigureAwait(false);
    }

    [Benchmark]
    public async ValueTask<IReadOnlyList<HybridMemorySearchResult>> Retrieve_WeightedLinearCombination_Top10()
    {
        return await _retriever.SearchHybridAsync("bench_ns", _linearQuery).ConfigureAwait(false);
    }
}
