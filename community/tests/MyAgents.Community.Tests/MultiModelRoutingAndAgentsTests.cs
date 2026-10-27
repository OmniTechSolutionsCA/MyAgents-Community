using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using MyAgents.Agents;
using MyAgents.Core;
using MyAgents.Core.Workflows;
using MyAgents.Testing;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class MultiModelRoutingAndAgentsTests
{
    private sealed class MockChatClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, ChatResponse> _handler;

        public MockChatClient(Func<IEnumerable<ChatMessage>, ChatResponse> handler)
        {
            _handler = handler;
        }

        public ChatClientMetadata Metadata => new("MockChatClient");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_handler(messages));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ChatResponseUpdate { Contents = [new TextContent("mock update")] };
        }

        public void Dispose() { }
    }

    private sealed class RecordingTelemetrySink : ITokenCostTelemetrySink
    {
        public List<ModelRoutingCostMetrics> Recorded { get; } = [];

        public ValueTask RecordMetricsAsync(ModelRoutingCostMetrics metrics, CancellationToken cancellationToken = default)
        {
            Recorded.Add(metrics);
            return ValueTask.CompletedTask;
        }
    }

    [Test]
    public async Task MultiModelRoutingChatClient_routes_by_heuristic_and_enforces_quota()
    {
        var economyClient = new MockChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "economy-response"))
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }
        });

        var standardClient = new MockChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "standard-response"))
        {
            Usage = new UsageDetails { InputTokenCount = 50, OutputTokenCount = 20 }
        });

        var advancedClient = new MockChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "advanced-response"))
        {
            Usage = new UsageDetails { InputTokenCount = 500, OutputTokenCount = 100 }
        });

        var sink = new RecordingTelemetrySink();
        var candidates = new List<ModelCandidate>
        {
            new("model-eco", economyClient, ModelTier.Economy, new ModelPricing("model-eco", 0.5m, 1.5m)),
            new("model-std", standardClient, ModelTier.Standard, new ModelPricing("model-std", 2.0m, 5.0m)),
            new("model-adv", advancedClient, ModelTier.Advanced, new ModelPricing("model-adv", 10.0m, 30.0m))
        };

        var options = new MultiModelRoutingOptions
        {
            MaxTokensPerTurn = 200 // will be violated if advanced client is called with >200 tokens
        };

        var router = new MultiModelRoutingChatClient(candidates, options, sink);

        // 1. Low complexity query -> Economy
        var ecoResponse = await router.GetResponseAsync([new ChatMessage(ChatRole.User, "extract and format json")]);
        Assert.That(ecoResponse.Messages[0].Text, Is.EqualTo("economy-response"));
        Assert.That(sink.Recorded.Last().SelectedTier, Is.EqualTo(ModelTier.Economy));

        // 2. Standard query -> Standard
        var stdResponse = await router.GetResponseAsync([new ChatMessage(ChatRole.User, "Please write a summary of the quarterly financial results.")]);
        Assert.That(stdResponse.Messages[0].Text, Is.EqualTo("standard-response"));
        Assert.That(sink.Recorded.Last().SelectedTier, Is.EqualTo(ModelTier.Standard));

        // 3. High complexity query -> Advanced, but triggers turn quota!
        var highComplexityText = "We need an in-depth architecture review for distributed consensus and formal proof " + new string('x', 2600);
        var quotaEx = Assert.ThrowsAsync<TokenBudgetQuotaException>(async () =>
            await router.GetResponseAsync([new ChatMessage(ChatRole.User, highComplexityText)]));

        Assert.That(quotaEx!.QuotaLimit, Is.EqualTo(200));
    }

    [Test]
    public async Task MultiModelRoutingChatClient_cascades_to_fallback_on_primary_failure()
    {
        var primaryFaulty = new MockChatClient(_ => throw new HttpRequestException("Primary provider unavailable"));
        var fallbackClient = new MockChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "fallback-recovered"))
        {
            Usage = new UsageDetails { InputTokenCount = 20, OutputTokenCount = 10 }
        });

        var sink = new RecordingTelemetrySink();
        var candidates = new List<ModelCandidate>
        {
            new("model-primary", primaryFaulty, ModelTier.Standard),
            new("model-backup", fallbackClient, ModelTier.Standard, IsFallback: true)
        };

        var router = new MultiModelRoutingChatClient(candidates, new MultiModelRoutingOptions { EnableFallbackCascade = true }, sink);
        var response = await router.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Multiple(() =>
        {
            Assert.That(response.Messages[0].Text, Is.EqualTo("fallback-recovered"));
            Assert.That(sink.Recorded, Has.Count.EqualTo(1));
            Assert.That(sink.Recorded[0].FallbackTriggered, Is.True);
            Assert.That(sink.Recorded[0].SelectedModelId, Is.EqualTo("model-backup"));
        });
    }

    [Test]
    public async Task HierarchicalSubagentDispatcher_dispatches_forks_and_guards_depth()
    {
        var runtime = new InMemoryAgentRuntime()
            .ForAgent("sub-analyst", q => q
                .EnqueueResponse("analyst findings")
                .EnqueueResponse("forked background findings"));

        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new DefaultAgentSessionFactory(runtime, services);
        var subagentDef = new AgentDefinition("sub-analyst", []);

        var dispatcher = new HierarchicalSubagentDispatcher(
            factory,
            [subagentDef],
            new SubagentOrchestratorOptions { MaxDelegationDepth = 2 });

        // Synchronous delegation
        var task = new SubagentTask(
            TaskId: "task-1",
            TargetAgentName: "sub-analyst",
            Instructions: "Analyze logs",
            CurrentDepth: 1);

        var result = await dispatcher.DelegateAsync(task);
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(SubagentExecutionState.Completed));
            Assert.That(result.FinalOutput, Is.EqualTo("analyst findings"));
        });

        // Depth guard
        var deepTask = task with { CurrentDepth = 3 };
        Assert.ThrowsAsync<MaxDelegationDepthExceededException>(async () =>
            await dispatcher.DelegateAsync(deepTask));

        // Background Fork
        var forkTask = task with { TaskId = "task-fork", Instructions = "Background research" };
        var handle = await dispatcher.ForkAsync(forkTask);
        Assert.That(handle.TaskId, Is.EqualTo("task-fork"));

        // Wait for background completion
        SubagentResult? forkResult = null;
        for (var i = 0; i < 20; i++)
        {
            forkResult = await dispatcher.GetTaskResultAsync("task-fork");
            if (forkResult?.Status == SubagentExecutionState.Completed)
                break;
            await Task.Delay(50);
        }

        Assert.That(forkResult, Is.Not.Null);
        Assert.That(forkResult!.Status, Is.EqualTo(SubagentExecutionState.Completed));
        Assert.That(forkResult.FinalOutput, Is.EqualTo("forked background findings"));
    }

    [Test]
    public async Task GroupChat_executes_multi_turn_round_robin_with_termination()
    {
        var runtime = new InMemoryAgentRuntime()
            .ForAgent("Alice", q => q.EnqueueResponse("Hello Bob").EnqueueResponse("I agree"))
            .ForAgent("Bob", q => q.EnqueueResponse("Hello Alice. COMPLETE"));

        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new DefaultAgentSessionFactory(runtime, services);

        var aliceDef = new AgentDefinition("Alice", []);
        var bobDef = new AgentDefinition("Bob", []);

        var groupChat = new GroupChat(
            new GroupChatOptions(
                Agents: [aliceDef, bobDef],
                MaxTurns: 10,
                Termination: new ContainsTextTermination("COMPLETE")),
            factory);

        var result = await groupChat.RunAsync([AgentMessage.FromText("user", "Start conversation")]);

        Assert.Multiple(() =>
        {
            // Initial + Alice (turn 1) + Bob (turn 2 with COMPLETE)
            Assert.That(result.TotalTurns, Is.EqualTo(2));
            Assert.That(result.LastSpeaker, Is.EqualTo("Bob"));
            Assert.That(result.Conversation.Last().Content, Does.Contain("COMPLETE"));
        });
    }

    private sealed class ContainsTextTermination(string text) : ITerminationCondition
    {
        public bool ShouldTerminate(AgentTurnResult result, int hopsCompletedSoFar)
        {
            return result.Output.Any(m => m.Content?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);
        }
    }
}
