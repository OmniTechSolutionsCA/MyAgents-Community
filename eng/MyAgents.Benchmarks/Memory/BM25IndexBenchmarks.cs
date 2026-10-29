using BenchmarkDotNet.Attributes;
using MyAgents.Memory;

namespace MyAgents.Benchmarks.Memory;

[MemoryDiagnoser]
public class BM25IndexBenchmarks
{
    [Params(1000, 10000)]
    public int DocumentCount { get; set; }

    private BM25Index _index = null!;
    private readonly string _shortQuery = "distributed consensus raft protocol";
    private readonly string _longQuery = "how does distributed consensus ensure leader election and log replication stability across high-latency network partitions with raft protocol quorum";
    private readonly string _newDocId = "doc_benchmark_incremental";
    private readonly string _newDocContent = "Distributed consensus algorithm ensures data consistency across replicated state machines even under network partition scenarios.";

    [GlobalSetup]
    public void Setup()
    {
        _index = new BM25Index();

        var vocabulary = new[]
        {
            "distributed", "systems", "consensus", "algorithm", "raft", "paxos", "state", "machine",
            "replication", "leader", "election", "log", "entries", "heartbeat", "timeout", "cluster",
            "node", "partition", "network", "failure", "recovery", "quorum", "commit", "index",
            "snapshot", "persistence", "storage", "performance", "throughput", "latency", "benchmark",
            "memory", "allocation", "garbage", "collection", "optimization", "parallel", "concurrency",
            "thread", "asynchronous", "task", "runtime", "pipeline", "execution", "message", "agent",
            "channel", "retrieval", "vector", "embedding", "semantic", "search", "lexical", "ranking"
        };

        var rng = new Random(42);
        for (var i = 0; i < DocumentCount; i++)
        {
            var wordCount = rng.Next(100, 150);
            var words = new string[wordCount];
            for (var w = 0; w < wordCount; w++)
            {
                words[w] = vocabulary[rng.Next(vocabulary.Length)];
            }
            var content = string.Join(" ", words);
            _index.AddOrUpdate($"doc_{i}", content);
        }
    }

    [Benchmark]
    public IReadOnlyList<(string DocId, double Score)> Search_ShortQuery_Top10()
    {
        return _index.Search(_shortQuery, maxResults: 10);
    }

    [Benchmark]
    public IReadOnlyList<(string DocId, double Score)> Search_LongQuery_Top10()
    {
        return _index.Search(_longQuery, maxResults: 10);
    }

    [Benchmark]
    public void AddOrUpdate_SingleDocument()
    {
        _index.AddOrUpdate(_newDocId, _newDocContent);
    }
}
