using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using MyAgents.Agents;
using MyAgents.Agents.Scrapai;
using MyAgents.Agents.WebSearch;
using MyAgents.Core;
using MyAgents.Core.Tools;
using MyAgents.Testing;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class GuardrailsAndRoutingTests
{
    private sealed class TestChatClient(Func<IEnumerable<ChatMessage>, ChatResponse> handler) : IChatClient
    {
        public ChatClientMetadata Metadata => new("TestChatClient");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(handler(chatMessages));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var resp = handler(chatMessages);
            yield return new ChatResponseUpdate(ChatRole.Assistant, resp.Text);
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class MockSearchProvider : IWebSearchProvider
    {
        public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<SearchResult> results =
            [
                new SearchResult("Result 1", "https://example.com/1", $"Snippet for {query}")
            ];
            return Task.FromResult(results);
        }
    }

    private sealed class MockProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ProcessResult(0, $"Executed {fileName} with {string.Join(' ', args)}", string.Empty));
        }
    }

    private sealed class MockSubagentDispatcher : ISubagentDispatcher
    {
        public IReadOnlyList<string> GetAvailableSubagentNames() => ["specialist", "background-worker"];

        public AgentDefinition? GetSubagentDefinition(string agentName) =>
            new AgentDefinition(agentName, []);

        public ValueTask<SubagentResult> DelegateAsync(SubagentTask task, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new SubagentResult(
                TaskId: task.TaskId,
                TargetAgentName: task.TargetAgentName,
                Status: SubagentExecutionState.Completed,
                FinalOutput: $"Delegated output for {task.TargetAgentName}",
                OutputMessages: [new AgentMessage("assistant", $"Output for {task.TargetAgentName}")],
                Duration: TimeSpan.FromMilliseconds(25),
                TotalTokens: 42));
        }

        public async IAsyncEnumerable<AgentTurnResult> DelegateStreamingAsync(
            SubagentTask task,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AgentTurnResult(
                [new AgentMessage("assistant", $"Output for {task.TargetAgentName}")],
                new AgentTurnTelemetry(TimeSpan.FromMilliseconds(10)));
            await Task.CompletedTask;
        }

        public ValueTask<SubagentTaskHandle> ForkAsync(SubagentTask task, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new SubagentTaskHandle(
                TaskId: task.TaskId,
                TargetAgentName: task.TargetAgentName,
                Status: SubagentExecutionState.Running,
                CreatedAt: DateTimeOffset.UtcNow));
        }

        public ValueTask<SubagentResult?> GetTaskResultAsync(string taskId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<SubagentResult?>(null);

        public ValueTask<bool> CancelTaskAsync(string taskId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
    }

    [Test]
    public async Task EmptyResponseGuardrailPolicy_FlagsEmptyOrWhitespace()
    {
        var policy = new EmptyResponseGuardrailPolicy();

        var emptyViolation = await policy.EvaluateAsync(Array.Empty<AgentMessage>());
        Assert.That(emptyViolation, Is.Not.Null);
        Assert.That(emptyViolation!.PolicyName, Is.EqualTo(nameof(EmptyResponseGuardrailPolicy)));

        var whitespaceViolation = await policy.EvaluateAsync([new AgentMessage("assistant", "   ")]);
        Assert.That(whitespaceViolation, Is.Not.Null);

        var validResult = await policy.EvaluateAsync([new AgentMessage("assistant", "Valid content")]);
        Assert.That(validResult, Is.Null);
    }

    [Test]
    public async Task MaxLengthGuardrailPolicy_FlagsExcessiveLength()
    {
        var policy = new MaxLengthGuardrailPolicy(maxLength: 10);

        var tooLong = await policy.EvaluateAsync([new AgentMessage("assistant", "This message exceeds 10 characters")]);
        Assert.That(tooLong, Is.Not.Null);
        Assert.That(tooLong!.PolicyName, Is.EqualTo(nameof(MaxLengthGuardrailPolicy)));

        var valid = await policy.EvaluateAsync([new AgentMessage("assistant", "Short")]);
        Assert.That(valid, Is.Null);
    }

    [Test]
    public async Task BlocklistGuardrailPolicy_FlagsConfiguredPhrases()
    {
        var policy = new BlocklistGuardrailPolicy(["password", "secret"]);

        var violation = await policy.EvaluateAsync([new AgentMessage("assistant", "Here is your Secret key")]);
        Assert.That(violation, Is.Not.Null);
        Assert.That(violation!.PolicyName, Is.EqualTo(nameof(BlocklistGuardrailPolicy)));
        Assert.That(violation.Reason, Contains.Substring("secret"));

        var valid = await policy.EvaluateAsync([new AgentMessage("assistant", "All clear and clean.")]);
        Assert.That(valid, Is.Null);
    }

    [Test]
    public async Task GuardrailMiddleware_ReplacesOutputOnViolation()
    {
        var feature = new GuardrailsFeature(new BlocklistGuardrailPolicy(["classified"]));
        var assembly = new AgentAssembly();
        feature.Contribute(assembly);
        var pipeline = assembly.Pipeline.Build();

        var services = new ServiceCollection().BuildServiceProvider();
        var context = new AgentTurnContext("sess-guardrail", [new AgentMessage("user", "Tell me")], new AgentTurnServices(services));

        var innerAgent = new DelegateAgent(ctx =>
            new AgentTurnResult(
                [new AgentMessage("assistant", "This document contains classified information.")],
                new AgentTurnTelemetry(TimeSpan.FromMilliseconds(5))));

        var result = await pipeline.InvokeAsync(innerAgent, context);

        Assert.That(result.Output.Count, Is.EqualTo(1));
        Assert.That(result.Output[0].Content, Contains.Substring("I'm sorry, but I cannot provide a response"));
    }

    [Test]
    public async Task CapabilityRouter_SelectsAgent_AndHandlesUnknownWithFallback()
    {
        var coderAgent = new AgentDefinition("CoderAgent", []);
        var analystAgent = new AgentDefinition("AnalystAgent", []);

        // 1. Matched selection
        var matchedClient = new TestChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "CoderAgent")));
        var router = new CapabilityRouter(matchedClient, [coderAgent, analystAgent]);

        var result = await router.RouteAsync("Write an NUnit test");
        Assert.That(result.SelectedAgent.Name, Is.EqualTo("CoderAgent"));

        // 2. Unmatched without fallback -> throws RoutingException
        var unmatchedClient = new TestChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "BakerAgent")));
        var strictRouter = new CapabilityRouter(unmatchedClient, [coderAgent, analystAgent]);

        Assert.ThrowsAsync<RoutingException>(async () =>
            await strictRouter.RouteAsync("Bake a cake"));

        // 3. Unmatched with fallback -> selects fallback agent
        var fallbackRouter = new CapabilityRouter(unmatchedClient, [coderAgent, analystAgent], fallbackAgentName: "AnalystAgent");
        var fallbackResult = await fallbackRouter.RouteAsync("Bake a cake");
        Assert.That(fallbackResult.SelectedAgent.Name, Is.EqualTo("AnalystAgent"));
    }

    [Test]
    public async Task SelectorGroupChat_DynamicSelectionAcrossTurns()
    {
        var runtime = new InMemoryAgentRuntime()
            .ForAgent("Alice", q => q.EnqueueResponse("Alice response 1").EnqueueResponse("Alice response 2"))
            .ForAgent("Bob", q => q.EnqueueResponse("Bob response 1"));

        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new DefaultAgentSessionFactory(runtime, services);

        var aliceDef = new AgentDefinition("Alice", []);
        var bobDef = new AgentDefinition("Bob", []);

        var callCount = 0;
        var selectorClient = new TestChatClient(_ =>
        {
            callCount++;
            var speaker = callCount == 1 ? "Alice" : "Bob";
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, speaker));
        });

        var groupChat = new SelectorGroupChat(
            new SelectorGroupChatOptions(
                Agents: [aliceDef, bobDef],
                SelectorClient: selectorClient,
                MaxTurns: 2),
            factory);

        var result = await groupChat.RunAsync([new AgentMessage("user", "Start conversation")]);

        Assert.That(result.TotalTurns, Is.EqualTo(2));
        Assert.That(result.LastSpeaker, Is.EqualTo("Bob"));
        Assert.That(result.SelectionReasoning.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task BuiltInTools_WebSearch_Scrapai_Subagent_ExecuteSuccessfully()
    {
        // 1. WebSearchTool
        var searchTool = new WebSearchTool(new MockSearchProvider());
        var searchJson = await searchTool.InvokeAsync("{\"Query\":\"dotnet 10\"}");
        Assert.That(searchJson, Contains.Substring("Result 1"));
        Assert.That(searchJson, Contains.Substring("dotnet 10"));

        var emptySearch = await searchTool.InvokeAsync("{}");
        Assert.That(emptySearch, Contains.Substring("required"));

        // 2. ScrapaiTool
        var scrapaiTool = new ScrapaiTool(new MockProcessRunner());
        var scrapaiJson = await scrapaiTool.InvokeAsync("{\"Args\":[\"crawl\",\"https://example.com\"]}");
        Assert.That(scrapaiJson, Contains.Substring("Executed scrapai"));

        // 3. DelegateToSubagentTool
        var dispatcher = new MockSubagentDispatcher();
        var delegateTool = new DelegateToSubagentTool(dispatcher);
        var delegateJson = await delegateTool.InvokeAsync(JsonSerializer.Serialize(new
        {
            target_agent = "specialist",
            instructions = "perform audit"
        }));
        Assert.That(delegateJson, Contains.Substring("completed"));
        Assert.That(delegateJson, Contains.Substring("specialist"));

        // 4. ForkSubagentTool
        var forkTool = new ForkSubagentTool(dispatcher);
        var forkJson = await forkTool.InvokeAsync(JsonSerializer.Serialize(new
        {
            target_agent = "background-worker",
            instructions = "process batch"
        }));
        Assert.That(forkJson, Contains.Substring("running"));
        Assert.That(forkJson, Contains.Substring("background-worker"));
    }

    private sealed class DelegateAgent(Func<AgentTurnContext, AgentTurnResult> handler) : IAgent
    {
        public ValueTask<AgentTurnResult> InvokeAsync(AgentTurnContext context, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(handler(context));
        }

        public async IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
            AgentTurnContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return await InvokeAsync(context, cancellationToken);
        }

        public void Dispose() { }
    }
}
