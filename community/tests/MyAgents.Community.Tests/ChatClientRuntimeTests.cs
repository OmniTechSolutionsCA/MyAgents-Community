using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyAgents.Core;
using MyAgents.Core.Tools;
using MyAgents.Runtime.Chat;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class ChatClientRuntimeTests
{
    [Test]
    public void AddMyAgentsChatRuntime_registers_the_runtime_and_configures_options()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new QueuedChatClient());
        services.AddMyAgentsChatRuntime(options => options.DefaultModelId = "configured-model");
        using var provider = services.BuildServiceProvider();

        Assert.Multiple(() =>
        {
            Assert.That(provider.GetRequiredService<IAgentRuntime>(), Is.InstanceOf<ChatClientRuntime>());
            Assert.That(
                provider.GetRequiredService<IOptions<ChatClientRuntimeOptions>>().Value.DefaultModelId,
                Is.EqualTo("configured-model"));
            Assert.That(provider.GetRequiredService<IAgentSessionFactory>(), Is.InstanceOf<DefaultAgentSessionFactory>());
        });
    }

    [Test]
    public async Task ChatRuntime_runs_chat_and_tools_with_the_provider_neutral_contracts()
    {
        var tool = new EchoTool();
        var observer = new RecordingToolObserver();
        var client = new QueuedChatClient();
        client.EnqueueResponse(ToolCallResponse("call-1", "echo", new Dictionary<string, object?> { ["text"] = "hello" }));
        client.EnqueueResponse(TextResponse("finished", inputTokens: 7, outputTokens: 4));

        var services = new ServiceCollection().BuildServiceProvider();
        var runtime = new ChatClientRuntime(client, Options.Create(new ChatClientRuntimeOptions
        {
            DefaultModelId = "default-model",
        }));
        var definition = new AgentDefinition(
            "community-agent",
            [new EchoFeature(tool, observer)],
            Capabilities: [new AgentModelCapability("agent-model")]);
        var session = await new DefaultAgentSessionFactory(runtime, services)
            .CreateSessionAsync("session-1", definition);

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "say hello")]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Output.Single().Content, Is.EqualTo("finished"));
            Assert.That(result.Telemetry.ToolCalls, Is.EqualTo(1));
            Assert.That(result.Telemetry.PromptTokens, Is.EqualTo(7));
            Assert.That(result.Telemetry.CompletionTokens, Is.EqualTo(4));
            Assert.That(result.Telemetry.ModelId, Is.EqualTo("agent-model"));
            Assert.That(tool.LastArguments, Is.EqualTo("{\"text\":\"hello\"}"));
            Assert.That(observer.Calls.Single().SessionId, Is.EqualTo("session-1"));
            Assert.That(client.Requests, Has.Count.EqualTo(2));
            Assert.That(client.Requests[0][0].Role, Is.EqualTo(ChatRole.System));
            Assert.That(client.Requests[0][0].Text, Is.EqualTo("Be concise."));
            Assert.That(client.Options[0].ModelId, Is.EqualTo("agent-model"));
            Assert.That(client.Options[0].Tools, Has.Count.EqualTo(1));
            Assert.That(client.Options[0].Tools![0].Name, Is.EqualTo("echo"));
            Assert.That(client.Requests[1].Last().Role, Is.EqualTo(ChatRole.Tool));
            Assert.That(
                client.Requests[1].Last().Contents.OfType<FunctionResultContent>().Single().Result,
                Is.EqualTo("tool result"));
        });
    }

    [Test]
    public async Task ChatRuntime_requires_policy_approval_before_executing_tools()
    {
        var tool = new EchoTool();
        var client = new QueuedChatClient();
        client.EnqueueResponse(ToolCallResponse("call-1", "echo", new Dictionary<string, object?> { ["text"] = "hello" }));

        var services = new ServiceCollection();
        services.AddSingleton<IToolApprovalPolicy>(new PendingApprovalPolicy());
        using var provider = services.BuildServiceProvider();
        var runtime = new ChatClientRuntime(client, Options.Create(new ChatClientRuntimeOptions()));
        var session = await new DefaultAgentSessionFactory(runtime, provider).CreateSessionAsync(
            "session-approval",
            new AgentDefinition("community-agent", [new EchoFeature(tool, observer: null)]));

        var exception = Assert.ThrowsAsync<ToolApprovalRequiredException>(async () =>
            await session.InvokeAsync([AgentMessage.FromText("user", "say hello")]));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Approval.Request.ToolName, Is.EqualTo("echo"));
            Assert.That(exception.Approval.Request.SessionId, Is.EqualTo("session-approval"));
            Assert.That(tool.LastArguments, Is.Null);
        });
    }

    [Test]
    public async Task ChatRuntime_rejects_denied_tool_without_invoking_it()
    {
        var tool = new EchoTool();
        var client = new QueuedChatClient();
        client.EnqueueResponse(ToolCallResponse("call-denied", "echo", new Dictionary<string, object?>()));
        var services = new ServiceCollection();
        services.AddSingleton<IToolApprovalPolicy>(new RuleBasedToolApprovalPolicy().DenyTool("echo"));
        using var provider = services.BuildServiceProvider();
        var runtime = new ChatClientRuntime(client, Options.Create(new ChatClientRuntimeOptions()));
        await using var session = await new DefaultAgentSessionFactory(runtime, provider)
            .CreateSessionAsync("denied-session", new AgentDefinition("agent", [new EchoFeature(tool, null)]));

        var error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync([AgentMessage.FromText("user", "call echo")]));

        Assert.That(error!.Message, Does.Contain("rejected"));
        Assert.That(tool.CallCount, Is.Zero);
    }

    [Test]
    public async Task ChatRuntime_rejects_unknown_tool_without_invoking_registered_tools()
    {
        var tool = new EchoTool();
        var client = new QueuedChatClient();
        client.EnqueueResponse(ToolCallResponse("call-unknown", "missing", new Dictionary<string, object?>()));
        using var provider = new ServiceCollection().BuildServiceProvider();
        var runtime = new ChatClientRuntime(client, Options.Create(new ChatClientRuntimeOptions()));
        await using var session = await new DefaultAgentSessionFactory(runtime, provider)
            .CreateSessionAsync("unknown-session", new AgentDefinition("agent", [new EchoFeature(tool, null)]));

        var error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync([AgentMessage.FromText("user", "call a tool")]));

        Assert.That(error!.Message, Does.Contain("unknown tool"));
        Assert.That(tool.CallCount, Is.Zero);
    }

    [Test]
    public async Task ChatRuntime_stops_repeated_tool_calls_at_configured_limit()
    {
        var tool = new EchoTool();
        var client = new QueuedChatClient();
        client.EnqueueResponse(ToolCallResponse("call-1", "echo", new Dictionary<string, object?>()));
        client.EnqueueResponse(ToolCallResponse("call-2", "echo", new Dictionary<string, object?>()));
        using var provider = new ServiceCollection().BuildServiceProvider();
        var runtime = new ChatClientRuntime(client, Options.Create(new ChatClientRuntimeOptions { MaxToolRoundTrips = 1 }));
        await using var session = await new DefaultAgentSessionFactory(runtime, provider)
            .CreateSessionAsync("limit-session", new AgentDefinition("agent", [new EchoFeature(tool, null)]));

        var error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync([AgentMessage.FromText("user", "call echo")]));

        Assert.That(error!.Message, Does.Contain("round-trip limit"));
        Assert.That(tool.CallCount, Is.EqualTo(1));
        Assert.That(client.Requests, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task ChatRuntime_honors_cancellation_before_contacting_provider()
    {
        var client = new QueuedChatClient();
        using var provider = new ServiceCollection().BuildServiceProvider();
        var runtime = new ChatClientRuntime(client, Options.Create(new ChatClientRuntimeOptions()));
        await using var session = await new DefaultAgentSessionFactory(runtime, provider)
            .CreateSessionAsync("cancel-session", new AgentDefinition("agent", []));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync([AgentMessage.FromText("user", "hello")], cancellation.Token));
        Assert.That(client.Requests, Is.Empty);
    }

    [Test]
    public async Task ChatRuntime_reports_tool_failure_to_observers_and_propagates_it()
    {
        var client = new QueuedChatClient();
        client.EnqueueResponse(ToolCallResponse("call-fail", "fail", new Dictionary<string, object?>()));
        var observer = new RecordingToolObserver();
        using var provider = new ServiceCollection().BuildServiceProvider();
        var runtime = new ChatClientRuntime(client, Options.Create(new ChatClientRuntimeOptions()));
        await using var session = await new DefaultAgentSessionFactory(runtime, provider)
            .CreateSessionAsync("failure-session", new AgentDefinition("agent", [new EchoFeature(new FailingTool(), observer)]));

        var error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync([AgentMessage.FromText("user", "call fail")]));

        Assert.That(error!.Message, Is.EqualTo("example tool failure"));
        Assert.That(observer.Results, Has.Count.EqualTo(1));
        Assert.That(observer.Results[0].Exception, Is.SameAs(error));
    }

    private static ChatResponse ToolCallResponse(string callId, string toolName, IDictionary<string, object?> arguments)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, toolName, arguments)]));

    private static ChatResponse TextResponse(string text, long inputTokens, long outputTokens)
        => new(new ChatMessage(ChatRole.Assistant, [new TextContent(text)]))
        {
            Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens },
        };

    private sealed class EchoFeature(ITool tool, IToolCallObserver? observer) : IAgentFeature
    {
        public void Contribute(AgentAssembly assembly)
        {
            assembly.Prompts.AddSystem("Be concise.");
            assembly.Tools.Add(tool);
            if (observer is not null)
                assembly.ToolObservers.Add(observer);
        }
    }

    private sealed class EchoTool : ITool
    {
        public string Name => "echo";
        public string? Description => "Echoes text.";
        public string? ParametersSchemaJson => "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"]}";
        public string? LastArguments { get; private set; }
        public int CallCount { get; private set; }

        public ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastArguments = jsonArguments;
            return ValueTask.FromResult("tool result");
        }
    }

    private sealed class FailingTool : ITool
    {
        public string Name => "fail";
        public string? Description => "A tool that fails.";
        public ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("example tool failure");
    }

    private sealed class RecordingToolObserver : IToolCallObserver
    {
        public List<ToolCallObservation> Calls { get; } = [];
        public List<ToolResultObservation> Results { get; } = [];

        public ValueTask OnToolCallingAsync(ToolCallObservation call, CancellationToken cancellationToken = default)
        {
            Calls.Add(call);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnToolCalledAsync(ToolResultObservation result, CancellationToken cancellationToken = default)
        {
            Results.Add(result);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PendingApprovalPolicy : IToolApprovalPolicy
    {
        public ValueTask<ToolApprovalDecision> DecideAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ToolApprovalDecision.Pending("review required"));
    }

    private sealed class QueuedChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new();

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public List<ChatOptions> Options { get; } = [];

        public void EnqueueResponse(ChatResponse response) => _responses.Enqueue(response);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(messages.ToArray());
            Options.Add(options ?? new ChatOptions());
            if (_responses.Count == 0)
                throw new InvalidOperationException("No fake chat response is queued.");
            return Task.FromResult(_responses.Dequeue());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        public void Dispose()
        {
        }
    }
}
