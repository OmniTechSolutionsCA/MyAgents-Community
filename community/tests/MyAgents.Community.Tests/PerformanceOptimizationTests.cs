using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MyAgents.Core;
using MyAgents.Core.Memory;
using MyAgents.Core.Tools;
using MyAgents.Core.Workflows;
using MyAgents.Memory;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class PerformanceOptimizationTests
{
    // =========================================================================
    // 1. DelegateTool Performance & Return Type Tests
    // =========================================================================

    [Test]
    public async Task DelegateTool_compiled_invoker_supports_void_task_and_valuetask()
    {
        var voidCalled = false;
        var voidTool = DelegateTool.Create(() => { voidCalled = true; });
        var voidRes = await voidTool.InvokeAsync("{}");
        Assert.That(voidCalled, Is.True);
        Assert.That(voidRes, Is.EqualTo("null"));

        var taskTool = DelegateTool.Create(async (int delayMs) =>
        {
            await Task.Yield();
            return delayMs * 2;
        });
        var taskRes = await taskTool.InvokeAsync("{\"delayMs\": 21}");
        Assert.That(taskRes, Is.EqualTo("42"));

        var vtaskTool = DelegateTool.Create((string msg) =>
        {
            return ValueTask.FromResult($"echo: {msg}");
        });
        var vtaskRes = await vtaskTool.InvokeAsync("{\"msg\": \"hello\"}");
        Assert.That(vtaskRes, Is.EqualTo("echo: hello"));

        var vtaskPlainTool = DelegateTool.Create(() => ValueTask.CompletedTask);
        var vtaskPlainRes = await vtaskPlainTool.InvokeAsync("{}");
        Assert.That(vtaskPlainRes, Is.EqualTo("true"));
    }

    // =========================================================================
    // 2. InMemoryMemoryStore Partitioning Tests
    // =========================================================================

    [Test]
    public async Task InMemoryMemoryStore_partitions_by_namespace_and_clears_isolated()
    {
        var store = new InMemoryMemoryStore();
        await store.SetAsync(new MemoryEntry("key1", "ns-a", "content a1"));
        await store.SetAsync(new MemoryEntry("key2", "ns-a", "content a2"));
        await store.SetAsync(new MemoryEntry("key1", "ns-b", "content b1"));

        Assert.That(store.Count, Is.EqualTo(3));

        var aHits = await store.SearchAsync("ns-a", maxResults: 10);
        Assert.That(aHits, Has.Count.EqualTo(2));
        Assert.That(aHits.All(h => h.Namespace == "ns-a"), Is.True);

        var bHits = await store.SearchAsync("ns-b", maxResults: 10);
        Assert.That(bHits, Has.Count.EqualTo(1));
        Assert.That(bHits[0].Content, Is.EqualTo("content b1"));

        await store.ClearAsync("ns-a");
        Assert.That(store.Count, Is.EqualTo(1));
        Assert.That(await store.GetAsync("ns-a", "key1"), Is.Null);
        Assert.That(await store.GetAsync("ns-b", "key1"), Is.Not.Null);
    }

    // =========================================================================
    // 3. BM25Index Top-K Min-Heap Ranking Tests
    // =========================================================================

    [Test]
    public void BM25Index_top_k_returns_results_in_descending_score_order()
    {
        var index = new BM25Index();
        for (var i = 0; i < 20; i++)
        {
            // Vary term frequencies to produce different BM25 scores
            var repeated = string.Join(" ", Enumerable.Repeat("keyword", i + 1));
            index.AddOrUpdate($"doc-{i}", $"common prefix {repeated} document content");
        }

        var top3 = index.Search("keyword", maxResults: 3);
        Assert.That(top3, Has.Count.EqualTo(3));
        Assert.That(top3[0].Score, Is.GreaterThanOrEqualTo(top3[1].Score));
        Assert.That(top3[1].Score, Is.GreaterThanOrEqualTo(top3[2].Score));
        // Highest repeat should have highest relevance score
        Assert.That(top3[0].DocId, Is.EqualTo("doc-19"));
    }

    // =========================================================================
    // 4. SqliteCheckpointer WAL & Synchronization Tests
    // =========================================================================

    [Test]
    public async Task SqliteCheckpointer_configures_wal_and_persists_snapshots()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"myagents_test_{Guid.NewGuid():N}.db");
        try
        {
            using (var checkpointer = new SqliteCheckpointer(dbPath))
            {
                var snapshot = new WorkflowSnapshot("wf-1", 1, "session-123", "{\"state\":\"active\"}");
                await checkpointer.SaveAsync(snapshot);

                var loaded = await checkpointer.LoadAsync("session-123");
                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded!.WorkflowId, Is.EqualTo("wf-1"));
                Assert.That(loaded.StateJson, Is.EqualTo("{\"state\":\"active\"}"));
            }

            // Verify journal_mode is WAL
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            var mode = cmd.ExecuteScalar()?.ToString();
            Assert.That(mode, Is.EqualTo("wal").IgnoreCase);
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { /* ignore on windows file lock */ }
            }
        }
    }

    // =========================================================================
    // 5. AgentPipeline Precomputed Chain Tests
    // =========================================================================

    [Test]
    public async Task AgentPipeline_build_chain_executes_in_order()
    {
        var executionLog = new List<string>();

        var middleware1 = new LoggingMiddleware("mw1", executionLog);
        var middleware2 = new LoggingMiddleware("mw2", executionLog);

        var pipeline = new PipelineBuilder().Use(middleware1).Use(middleware2).Build();
        var innerAgent = new MockAgent(executionLog);

        var chain = pipeline.BuildChain(innerAgent);
        var services = new AgentTurnServices(new ServiceCollection().BuildServiceProvider());
        var context = new AgentTurnContext("sess-1", [AgentMessage.FromText("user", "hi")], services);

        var result = await chain(context, CancellationToken.None);

        Assert.That(result.Output[0].Content, Is.EqualTo("inner-agent-reply"));
        Assert.That(executionLog, Is.EqualTo(new[] { "mw1-before", "mw2-before", "inner-agent", "mw2-after", "mw1-after" }));
    }

    private sealed class LoggingMiddleware(string name, List<string> log) : IAgentMiddleware
    {
        public async ValueTask<AgentTurnResult> InvokeAsync(
            AgentTurnContext context,
            Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>> next,
            CancellationToken cancellationToken = default)
        {
            log.Add($"{name}-before");
            var result = await next(context, cancellationToken);
            log.Add($"{name}-after");
            return result;
        }

        public async IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
            AgentTurnContext context,
            Func<AgentTurnContext, CancellationToken, IAsyncEnumerable<AgentTurnResult>> next,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            log.Add($"{name}-before");
            await foreach (var item in next(context, cancellationToken))
            {
                yield return item;
            }
            log.Add($"{name}-after");
        }
    }

    private sealed class MockAgent(List<string> log) : IAgent
    {
        private static readonly AgentTurnTelemetry Telemetry = new(TimeSpan.Zero, 0, null, null, null);

        public ValueTask<AgentTurnResult> InvokeAsync(AgentTurnContext context, CancellationToken cancellationToken = default)
        {
            log.Add("inner-agent");
            return ValueTask.FromResult(new AgentTurnResult([AgentMessage.FromText("assistant", "inner-agent-reply")], Telemetry));
        }

        public async IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
            AgentTurnContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            log.Add("inner-agent");
            yield return new AgentTurnResult([AgentMessage.FromText("assistant", "inner-agent-reply")], Telemetry);
            await Task.Yield();
        }

        public void Dispose() { }
    }
}
