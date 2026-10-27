using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyAgents.Core;
using MyAgents.Core.Tools;
using MyAgents.Runtime.Chat;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class ChatClientRuntimeExtendedTests
{
    private sealed class MultiTurnMockChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new();
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public void Enqueue(ChatResponse response) => _responses.Enqueue(response);

        public ChatClientMetadata Metadata => new("MultiTurnMockChatClient");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            return Task.FromResult(_responses.Dequeue());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public void Dispose() { }
    }

    private sealed class MultiArgTool(string name) : ITool
    {
        public string Name => name;
        public string? Description => $"Tool {name}";
        public string? ParametersSchemaJson => null;
        public int InvocationCount { get; private set; }

        public ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return ValueTask.FromResult($"Executed {name}");
        }
    }

    private sealed class ToolFeature(params ITool[] tools) : IAgentFeature
    {
        public void Contribute(AgentAssembly assembly)
        {
            foreach (var t in tools)
                assembly.Tools.Add(t);
        }
    }

    [Test]
    public void Constructor_validates_runtime_options()
    {
        var client = new MultiTurnMockChatClient();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChatClientRuntime(client, Options.Create(new ChatClientRuntimeOptions { MaxToolRoundTrips = 0 })));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChatClientRuntime(client, Options.Create(new ChatClientRuntimeOptions { MaxOutputTokens = -10 })));
    }

    [Test]
    public async Task ChatRuntime_forwards_multimodal_content_parts_to_chat_messages()
    {
        var client = new MultiTurnMockChatClient();
        client.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Got the files.")));

        var runtime = new ChatClientRuntime(client, Options.Create(new ChatClientRuntimeOptions()));
        var services = new ServiceCollection().BuildServiceProvider();
        var session = await new DefaultAgentSessionFactory(runtime, services)
            .CreateSessionAsync("multimodal-session", new AgentDefinition("agent", []));

        var imageBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
        var parts = new List<AgentContent>
        {
            new TextAgentContent("Inspect this image and document"),
            new ImageAgentContent(imageBytes, "image/jpeg"),
            new UriImageAgentContent(new Uri("https://example.com/chart.png"))
        };
        var message = AgentMessage.FromParts("user", parts, textSummary: "Inspect this image and document");

        var result = await session.InvokeAsync([message]);

        Assert.That(result.Output.Single().Content, Is.EqualTo("Got the files."));
        Assert.That(client.Requests, Has.Count.EqualTo(1));

        var sentMessage = client.Requests[0].Single();
        Assert.That(sentMessage.Contents, Has.Count.EqualTo(3));
        Assert.Multiple(() =>
        {
            Assert.That(sentMessage.Contents[0], Is.InstanceOf<TextContent>());
            Assert.That(sentMessage.Contents[1], Is.InstanceOf<DataContent>());
            Assert.That(((DataContent)sentMessage.Contents[1]).MediaType, Is.EqualTo("image/jpeg"));
            Assert.That(sentMessage.Contents[2], Is.InstanceOf<TextContent>());
            Assert.That(((TextContent)sentMessage.Contents[2]).Text, Is.EqualTo("https://example.com/chart.png"));
        });
    }

    [Test]
    public async Task ChatRuntime_executes_multiple_parallel_function_calls_in_single_turn()
    {
        var tool1 = new MultiArgTool("tool_alpha");
        var tool2 = new MultiArgTool("tool_beta");

        var client = new MultiTurnMockChatClient();
        // Turn 1 returns 2 function calls
        client.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("call-1", "tool_alpha", new Dictionary<string, object?> { ["x"] = 1 }),
            new FunctionCallContent("call-2", "tool_beta", new Dictionary<string, object?> { ["y"] = 2 })
        ])));
        // Turn 2 returns final answer
        client.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Both tools completed.")));

        var runtime = new ChatClientRuntime(client, Options.Create(new ChatClientRuntimeOptions()));
        var services = new ServiceCollection().BuildServiceProvider();
        var session = await new DefaultAgentSessionFactory(runtime, services)
            .CreateSessionAsync("parallel-tools-session", new AgentDefinition("agent", [new ToolFeature(tool1, tool2)]));

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "Run both tools")]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Output.Single().Content, Is.EqualTo("Both tools completed."));
            Assert.That(result.Telemetry.ToolCalls, Is.EqualTo(2));
            Assert.That(tool1.InvocationCount, Is.EqualTo(1));
            Assert.That(tool2.InvocationCount, Is.EqualTo(1));
            Assert.That(client.Requests, Has.Count.EqualTo(2));
        });
    }
}
