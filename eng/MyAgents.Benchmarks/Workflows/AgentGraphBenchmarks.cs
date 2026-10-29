using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using MyAgents.Core;
using MyAgents.Core.Workflows;
using MyAgents.Testing;

namespace MyAgents.Benchmarks.Workflows;

[MemoryDiagnoser]
public class AgentGraphBenchmarks
{
    public sealed record GraphState(int Step, string LastNode);

    private AgentGraph<GraphState> _graph = null!;
    private readonly GraphState _initialState = new(0, "Start");

    [GlobalSetup]
    public void Setup()
    {
        var runtime = new InMemoryAgentRuntime();
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new DefaultAgentSessionFactory(runtime, services);

        var definitions = Enumerable.Range(0, 10)
            .Select(i => new AgentDefinition($"Node_{i}", []))
            .ToArray();

        var historyStore = new InMemoryGraphHistoryStore();

        _graph = new AgentGraph<GraphState>(
            factory,
            definitions,
            merge: (_, next) => next,
            historyStore: historyStore);

        for (var i = 0; i < 10; i++)
        {
            var nodeName = $"Node_{i}";
            var nextIndex = i + 1;
            var nextNodeName = nextIndex < 10 ? $"Node_{nextIndex}" : AgentGraph<GraphState>.End;
            _graph.AddNode(nodeName, (state, _, _) => ValueTask.FromResult(new GraphState(state.Step + 1, nodeName)));

            if (i % 2 == 0 && i < 9)
            {
                _graph.AddConditionalEdge(nodeName, state => state.Step >= 0 ? nextNodeName : AgentGraph<GraphState>.End);
            }
            else
            {
                _graph.AddEdge(nodeName, nextNodeName);
            }
        }
    }

    [Benchmark]
    public async ValueTask<GraphRunResult<GraphState>> AgentGraph_10NodeConditionalExecution()
    {
        return await _graph.RunAsync("Node_0", _initialState, []).ConfigureAwait(false);
    }
}
