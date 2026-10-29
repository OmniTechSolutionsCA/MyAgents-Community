using BenchmarkDotNet.Attributes;
using MyAgents.Core.Workflows;

namespace MyAgents.Benchmarks.Workflows;

[MemoryDiagnoser]
public class SqliteCheckpointerBenchmarks
{
    private string _dbPath = null!;
    private SqliteCheckpointer _checkpointer = null!;
    private WorkflowSnapshot _snapshot = null!;
    private const string SessionId = "session_bench_123";

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"myagents_bench_{Guid.NewGuid():N}.db");
        _checkpointer = new SqliteCheckpointer(_dbPath);

        // 4 KB dummy JSON payload
        var stateJson = "{\"data\":\"" + new string('A', 4000) + "\"}";
        _snapshot = new WorkflowSnapshot(
            WorkflowId: "benchmark_workflow",
            StateVersion: 1,
            SessionId: SessionId,
            StateJson: stateJson);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _checkpointer.Dispose();
        try
        {
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
        catch
        {
            // Ignore cleanup errors for temp file
        }
    }

    [Benchmark]
    public async ValueTask<WorkflowSnapshot?> SqliteCheckpointer_SaveAndLoad()
    {
        await _checkpointer.SaveAsync(_snapshot).ConfigureAwait(false);
        return await _checkpointer.LoadAsync(SessionId).ConfigureAwait(false);
    }
}
