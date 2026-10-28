using Microsoft.Extensions.DependencyInjection;
using MyAgents.Core;
using MyAgents.Core.Workflows;
using MyAgents.Testing;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class AgentGraphTests
{
    private record TestGraphState(int Counter = 0, string LastNode = "")
    {
        public List<string> LogList { get; init; } = [];
    }

    private static (DefaultAgentSessionFactory Factory, AgentDefinition Agent1, AgentDefinition Agent2, AgentDefinition Agent3) CreateTestFixtures()
    {
        var runtime = new InMemoryAgentRuntime();
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new DefaultAgentSessionFactory(runtime, services);
        var def1 = new AgentDefinition("Node1", []);
        var def2 = new AgentDefinition("Node2", []);
        var def3 = new AgentDefinition("Node3", []);
        return (factory, def1, def2, def3);
    }

    [Test]
    public async Task RunAsync_executes_sequential_nodes_and_reaches_End()
    {
        var (factory, def1, def2, _) = CreateTestFixtures();
        var graph = new AgentGraph<TestGraphState>(
            factory,
            [def1, def2],
            merge: (prev, next) => next);

        graph.AddNode("Node1", (state, _, _) =>
        {
            var nextState = state with { Counter = state.Counter + 1, LastNode = "Node1" };
            nextState.LogList.Add("Node1");
            return ValueTask.FromResult(nextState);
        });

        graph.AddNode("Node2", (state, _, _) =>
        {
            var nextState = state with { Counter = state.Counter + 10, LastNode = "Node2" };
            nextState.LogList.Add("Node2");
            return ValueTask.FromResult(nextState);
        });

        graph.AddEdge("Node1", "Node2");
        graph.AddEdge("Node2", AgentGraph<TestGraphState>.End);

        var result = await graph.RunAsync("Node1", new TestGraphState(), []);

        Assert.Multiple(() =>
        {
            Assert.That(result.Reason, Is.EqualTo(GraphRunReason.Completed));
            Assert.That(result.IterationsExecuted, Is.EqualTo(2));
            Assert.That(result.FinalState.Counter, Is.EqualTo(11));
            Assert.That(result.FinalState.LastNode, Is.EqualTo("Node2"));
            Assert.That(result.FinalState.LogList, Is.EqualTo(new[] { "Node1", "Node2" }));
        });
    }

    [Test]
    public async Task RunAsync_routes_conditionally_based_on_state()
    {
        var (factory, def1, def2, def3) = CreateTestFixtures();
        var graph = new AgentGraph<TestGraphState>(
            factory,
            [def1, def2, def3],
            merge: (_, next) => next);

        graph.AddNode("Node1", (state, _, _) => ValueTask.FromResult(state with { LastNode = "Node1" }));
        graph.AddNode("Node2", (state, _, _) => ValueTask.FromResult(state with { Counter = 200, LastNode = "Node2" }));
        graph.AddNode("Node3", (state, _, _) => ValueTask.FromResult(state with { Counter = 300, LastNode = "Node3" }));

        graph.AddConditionalEdge("Node1", state => state.Counter > 50 ? "Node2" : "Node3");
        graph.AddEdge("Node2", AgentGraph<TestGraphState>.End);
        graph.AddEdge("Node3", AgentGraph<TestGraphState>.End);

        var resultLow = await graph.RunAsync("Node1", new TestGraphState(Counter: 10), []);
        var resultHigh = await graph.RunAsync("Node1", new TestGraphState(Counter: 100), []);

        Assert.Multiple(() =>
        {
            Assert.That(resultLow.FinalState.Counter, Is.EqualTo(300));
            Assert.That(resultLow.FinalState.LastNode, Is.EqualTo("Node3"));
            Assert.That(resultHigh.FinalState.Counter, Is.EqualTo(200));
            Assert.That(resultHigh.FinalState.LastNode, Is.EqualTo("Node2"));
        });
    }

    [Test]
    public async Task RunAsync_executes_fan_out_parallel_nodes_and_merges_states()
    {
        var (factory, def1, def2, def3) = CreateTestFixtures();
        var graph = new AgentGraph<TestGraphState>(
            factory,
            [def1, def2, def3],
            merge: (curr, branch) =>
            {
                var combinedLog = new List<string>(curr.LogList);
                combinedLog.AddRange(branch.LogList);
                return new TestGraphState(
                    Counter: curr.Counter + branch.Counter,
                    LastNode: branch.LastNode)
                {
                    LogList = combinedLog
                };
            });

        graph.AddNode("Node1", (state, _, _) =>
            ValueTask.FromResult(new TestGraphState(Counter: 1, LastNode: "Node1") { LogList = ["Node1"] }));

        graph.AddNode("Node2", (state, _, _) =>
            ValueTask.FromResult(new TestGraphState(Counter: 10, LastNode: "Node2") { LogList = ["Node2"] }));

        graph.AddNode("Node3", (state, _, _) =>
            ValueTask.FromResult(new TestGraphState(Counter: 100, LastNode: "Node3") { LogList = ["Node3"] }));

        graph.AddFanOutEdge("Node1", "Node2", "Node3");
        graph.AddEdge("Node1", AgentGraph<TestGraphState>.End);

        var result = await graph.RunAsync("Node1", new TestGraphState(), []);

        Assert.Multiple(() =>
        {
            Assert.That(result.Reason, Is.EqualTo(GraphRunReason.Completed));
            // Primary Node1 + 2 fanout nodes = 3 transitions
            Assert.That(result.IterationsExecuted, Is.EqualTo(3));
            Assert.That(result.FinalState.Counter, Is.EqualTo(111));
            Assert.That(result.FinalState.LogList, Does.Contain("Node1"));
            Assert.That(result.FinalState.LogList, Does.Contain("Node2"));
            Assert.That(result.FinalState.LogList, Does.Contain("Node3"));
        });
    }

    [Test]
    public async Task RunAsync_routes_dynamic_send_edge_packets()
    {
        var (factory, def1, def2, def3) = CreateTestFixtures();
        var graph = new AgentGraph<TestGraphState>(
            factory,
            [def1, def2, def3],
            merge: (curr, branch) => new TestGraphState(
                Counter: curr.Counter + branch.Counter,
                LastNode: branch.LastNode));

        graph.AddNode("Node1", (state, _, _) => ValueTask.FromResult(state with { LastNode = "Node1" }));
        graph.AddNode("Node2", (state, _, _) => ValueTask.FromResult(new TestGraphState(Counter: state.Counter * 2, LastNode: "Node2")));
        graph.AddNode("Node3", (state, _, _) => ValueTask.FromResult(new TestGraphState(Counter: state.Counter * 3, LastNode: "Node3")));

        graph.AddSendEdge("Node1", state =>
        [
            new SendPacket<TestGraphState>("Node2", new TestGraphState(Counter: 5)),
            new SendPacket<TestGraphState>("Node3", new TestGraphState(Counter: 10))
        ]);
        graph.AddEdge("Node1", AgentGraph<TestGraphState>.End);

        var result = await graph.RunAsync("Node1", new TestGraphState(Counter: 1), []);

        Assert.Multiple(() =>
        {
            // Node1 (initial 1 + nodeOutput 1 = 2) -> Dynamic branches: Node2 (5 * 2 = 10 -> 12) + Node3 (10 * 3 = 30 -> 42)
            Assert.That(result.FinalState.Counter, Is.EqualTo(42));
            Assert.That(result.IterationsExecuted, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task RunAsync_executes_nested_subgraph_and_merges_back()
    {
        var (factory, def1, def2, _) = CreateTestFixtures();
        var innerDef = new AgentDefinition("InnerNode", []);

        var innerGraph = new AgentGraph<int>(
            factory,
            [innerDef],
            merge: (_, next) => next);

        innerGraph.AddNode("InnerNode", (num, _, _) => ValueTask.FromResult(num * 5));
        innerGraph.AddEdge("InnerNode", AgentGraph<int>.End);

        var outerGraph = new AgentGraph<TestGraphState>(
            factory,
            [def1, def2],
            merge: (_, next) => next);

        outerGraph.AddNode("Node1", (state, _, _) => ValueTask.FromResult(state with { Counter = 7 }));
        outerGraph.AddSubgraph(
            "InnerNode",
            innerGraph,
            subgraphStartNode: "InnerNode",
            mapIn: outer => outer.Counter,
            mapOut: (outer, innerFinal) => outer with { Counter = innerFinal, LastNode = "Subgraph" });

        outerGraph.AddEdge("Node1", "InnerNode");
        outerGraph.AddEdge("InnerNode", AgentGraph<TestGraphState>.End);

        var result = await outerGraph.RunAsync("Node1", new TestGraphState(), []);

        Assert.Multiple(() =>
        {
            Assert.That(result.Reason, Is.EqualTo(GraphRunReason.Completed));
            Assert.That(result.FinalState.Counter, Is.EqualTo(35));
            Assert.That(result.FinalState.LastNode, Is.EqualTo("Subgraph"));
        });
    }

    [Test]
    public async Task RunAsync_stops_with_MaxIterationsReached_on_cycle()
    {
        var (factory, def1, def2, _) = CreateTestFixtures();
        var graph = new AgentGraph<TestGraphState>(
            factory,
            [def1, def2],
            merge: (_, next) => next,
            maxIterations: 5);

        graph.AddNode("Node1", (state, _, _) => ValueTask.FromResult(state with { Counter = state.Counter + 1 }));
        graph.AddNode("Node2", (state, _, _) => ValueTask.FromResult(state with { Counter = state.Counter + 1 }));

        graph.AddEdge("Node1", "Node2");
        graph.AddEdge("Node2", "Node1"); // Infinite loop

        var result = await graph.RunAsync("Node1", new TestGraphState(), []);

        Assert.Multiple(() =>
        {
            Assert.That(result.Reason, Is.EqualTo(GraphRunReason.MaxIterationsReached));
            Assert.That(result.IterationsExecuted, Is.EqualTo(5));
        });
    }

    [Test]
    public async Task RunStreamingAsync_yields_events_for_each_node_and_terminal_event()
    {
        var (factory, def1, def2, _) = CreateTestFixtures();
        var graph = new AgentGraph<TestGraphState>(
            factory,
            [def1, def2],
            merge: (_, next) => next);

        graph.AddNode("Node1", (state, _, _) => ValueTask.FromResult(state with { Counter = 1, LastNode = "Node1" }));
        graph.AddNode("Node2", (state, _, _) => ValueTask.FromResult(state with { Counter = 2, LastNode = "Node2" }));
        graph.AddEdge("Node1", "Node2");
        graph.AddEdge("Node2", AgentGraph<TestGraphState>.End);

        var events = new List<GraphStreamEvent<TestGraphState>>();
        await foreach (var evt in graph.RunStreamingAsync("Node1", new TestGraphState(), []))
        {
            events.Add(evt);
        }

        Assert.Multiple(() =>
        {
            Assert.That(events, Has.Count.EqualTo(3));
            Assert.That(events[0].NodeName, Is.EqualTo("Node1"));
            Assert.That(events[0].IsFinal, Is.False);
            Assert.That(events[1].NodeName, Is.EqualTo("Node2"));
            Assert.That(events[1].IsFinal, Is.False);
            Assert.That(events[2].NodeName, Is.EqualTo(AgentGraph<TestGraphState>.End));
            Assert.That(events[2].IsFinal, Is.True);
            Assert.That(events[2].Reason, Is.EqualTo(GraphRunReason.Completed));
        });
    }

    [Test]
    public async Task ResumeFromSnapshotAsync_time_travels_and_replays_from_persisted_snapshot()
    {
        var (factory, def1, def2, _) = CreateTestFixtures();
        var historyStore = new InMemoryGraphHistoryStore();

        var graph = new AgentGraph<TestGraphState>(
            factory,
            [def1, def2],
            merge: (_, next) => next,
            historyStore: historyStore);

        graph.AddNode("Node1", (state, _, _) => ValueTask.FromResult(state with { Counter = 10, LastNode = "Node1" }));
        graph.AddNode("Node2", (state, _, _) => ValueTask.FromResult(state with { Counter = state.Counter + 5, LastNode = "Node2" }));
        graph.AddEdge("Node1", "Node2");
        graph.AddEdge("Node2", AgentGraph<TestGraphState>.End);

        var firstRun = await graph.RunAsync("Node1", new TestGraphState(), [], graphSessionId: "session-hist");
        Assert.That(firstRun.FinalState.Counter, Is.EqualTo(15));

        var snapshots = await historyStore.ListSnapshotsAsync("session-hist");
        Assert.That(snapshots, Has.Count.EqualTo(2));
        var node1Snapshot = snapshots.First(s => s.NodeName == "Node1");

        // Replay/branch from Node1 snapshot into Node2 with new branch session
        var replayedResult = await graph.ResumeFromSnapshotAsync(
            node1Snapshot.SnapshotId,
            startNode: "Node2",
            newSessionId: "branch-session");

        Assert.Multiple(() =>
        {
            Assert.That(replayedResult.Reason, Is.EqualTo(GraphRunReason.Completed));
            Assert.That(replayedResult.FinalState.Counter, Is.EqualTo(15));
            Assert.That(replayedResult.IterationsExecuted, Is.EqualTo(1));
        });

        var branchSnapshots = await historyStore.ListSnapshotsAsync("branch-session");
        Assert.That(branchSnapshots, Has.Count.EqualTo(1));
        Assert.That(branchSnapshots[0].ReplayedFromSnapshotId, Is.EqualTo(node1Snapshot.SnapshotId));
    }

    [Test]
    public void ToMermaid_and_ToDot_export_valid_topologies()
    {
        var (factory, def1, def2, def3) = CreateTestFixtures();
        var graph = new AgentGraph<TestGraphState>(
            factory,
            [def1, def2, def3],
            merge: (_, next) => next);

        graph.AddNode("Node1", (s, _, _) => ValueTask.FromResult(s));
        graph.AddNode("Node2", (s, _, _) => ValueTask.FromResult(s));
        graph.AddNode("Node3", (s, _, _) => ValueTask.FromResult(s));

        graph.AddEdge("Node1", "Node2");
        graph.AddConditionalEdge("Node2", _ => "Node3");
        graph.AddFanOutEdge("Node3", "Node1", "Node2");

        var mermaid = graph.ToMermaid();
        var dot = graph.ToDot("TestDiagram");

        Assert.Multiple(() =>
        {
            Assert.That(mermaid, Does.StartWith("graph TD"));
            Assert.That(mermaid, Does.Contain("Node1 --> Node2"));
            Assert.That(mermaid, Does.Contain("Node2 -.->|condition| Node2_router"));
            Assert.That(mermaid, Does.Contain("==>|fan-out|"));

            Assert.That(dot, Does.StartWith("digraph TestDiagram {"));
            Assert.That(dot, Does.Contain("\"Node1\" -> \"Node2\";"));
            Assert.That(dot, Does.Contain("label=\"fan-out\""));
        });
    }

    [Test]
    public void AgentGraph_ToMermaid_sanitizes_complex_node_names_with_spaces_dots_and_dashes()
    {
        var (factory, _, _, _) = CreateTestFixtures();
        var defA = new AgentDefinition("Alpha Beta", []);
        var defB = new AgentDefinition("Gamma-Delta.1", []);
        var graph = new AgentGraph<TestGraphState>(
            factory,
            [defA, defB],
            merge: (_, next) => next);

        graph.AddNode("Alpha Beta", (s, _, _) => ValueTask.FromResult(s));
        graph.AddNode("Gamma-Delta.1", (s, _, _) => ValueTask.FromResult(s));
        graph.AddEdge("Alpha Beta", "Gamma-Delta.1");

        var mermaid = graph.ToMermaid();
        Assert.Multiple(() =>
        {
            Assert.That(mermaid, Does.Contain("Alpha_Beta[\"Alpha Beta\"]"));
            Assert.That(mermaid, Does.Contain("Gamma_Delta_1[\"Gamma-Delta.1\"]"));
            Assert.That(mermaid, Does.Contain("Alpha_Beta --> Gamma_Delta_1"));
        });
    }

    [Test]
    public void ConsensusSteps_ReduceMajority_identifies_winner_and_tie_breaks()
    {
        var turnResult = new AgentTurnResult([], new AgentTurnTelemetry(TimeSpan.Zero, 0, null, null, null));
        var candidates = new List<ConsensusCandidate>
        {
            new("Agent1", turnResult, "OptionA"),
            new("Agent2", turnResult, "OptionB"),
            new("Agent3", turnResult, "OptionA")
        };

        var majorityResult = ConsensusSteps.ReduceMajority(candidates);
        Assert.Multiple(() =>
        {
            Assert.That(majorityResult.WinningChoice, Is.EqualTo("OptionA"));
            Assert.That(majorityResult.WinningCandidates, Has.Count.EqualTo(2));
            Assert.That(majorityResult.TieBroken, Is.False);
        });

        // Test tie
        var tiedCandidates = new List<ConsensusCandidate>
        {
            new("Agent1", turnResult, "OptionA"),
            new("Agent2", turnResult, "OptionB")
        };

        var tiedResult = ConsensusSteps.ReduceMajority(tiedCandidates, tieBreaker: ties => ties.First(c => c.Choice == "OptionB"));
        Assert.Multiple(() =>
        {
            Assert.That(tiedResult.WinningChoice, Is.EqualTo("OptionB"));
            Assert.That(tiedResult.TieBroken, Is.True);
        });
    }
}
