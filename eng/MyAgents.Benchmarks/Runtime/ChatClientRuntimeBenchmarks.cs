using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyAgents.Core;
using MyAgents.Core.Tools;
using MyAgents.Runtime.Chat;

namespace MyAgents.Benchmarks.Runtime;

[MemoryDiagnoser]
public class ChatClientRuntimeBenchmarks
{
    private sealed class FastChatClient : IChatClient
    {
        private readonly ChatResponse _textResponse;
        private readonly ChatResponse? _toolResponse;
        private readonly ChatResponseUpdate[] _streamingUpdates;

        public FastChatClient(ChatResponse? toolResponse = null, int streamChunkCount = 50)
        {
            _textResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, [new TextContent("Finished turn response.")]));
            _toolResponse = toolResponse;
            _streamingUpdates = Enumerable.Range(0, streamChunkCount)
                .Select(i => new ChatResponseUpdate(ChatRole.Assistant, $"token_{i} "))
                .ToArray();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (_toolResponse is not null && !messages.Any(static m => m.Role == ChatRole.Tool))
            {
                return Task.FromResult(_toolResponse);
            }

            return Task.FromResult(_textResponse);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < _streamingUpdates.Length; i++)
            {
                yield return _streamingUpdates[i];
            }
        }
    }

    private sealed class FastEchoTool(string name) : ITool
    {
        public string Name => name;
        public string? Description => "Fast benchmark tool";
        public string? ParametersSchemaJson => "{\"type\":\"object\"}";

        public ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
            => ValueTask.FromResult("{\"status\":\"ok\"}");
    }

    private IAgent _noToolsAgent = null!;
    private IAgent _singleToolAgent = null!;
    private IAgent _fiveToolsAgent = null!;
    private IAgent _streamingAgent = null!;
    private AgentTurnContext _turnContext = null!;

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var turnServices = new AgentTurnServices(services);

        var messages = Enumerable.Range(0, 10)
            .Select(i => AgentMessage.FromText(i % 2 == 0 ? "user" : "assistant", $"Message history entry number {i}"))
            .ToArray();

        _turnContext = new AgentTurnContext("benchmark-session", messages, turnServices);

        // 1. No tools
        var clientNoTools = new FastChatClient();
        var runtimeNoTools = new ChatClientRuntime(clientNoTools, Options.Create(new ChatClientRuntimeOptions()));
        var defNoTools = new AgentDefinition("no-tools-agent", []);
        var assemblyNoTools = new AgentAssembly();
        _noToolsAgent = runtimeNoTools.CreateAgent(defNoTools, assemblyNoTools);

        // 2. Single tool
        var singleTool = new FastEchoTool("tool_0");
        var singleToolCall = new ChatResponse(new ChatMessage(ChatRole.Assistant, [
            new FunctionCallContent("call_0", "tool_0", new Dictionary<string, object?> { ["x"] = 1 })
        ]));
        var clientSingleTool = new FastChatClient(singleToolCall);
        var runtimeSingleTool = new ChatClientRuntime(clientSingleTool, Options.Create(new ChatClientRuntimeOptions
        {
            MaxParallelToolCalls = 4,
            MaxToolRoundTrips = 2,
        }));
        var assemblySingle = new AgentAssembly();
        assemblySingle.Tools.Add(singleTool);
        _singleToolAgent = runtimeSingleTool.CreateAgent(new AgentDefinition("single-tool-agent", []), assemblySingle);

        // 3. Five parallel tools
        var fiveCalls = Enumerable.Range(0, 5)
            .Select(i => (AIContent)new FunctionCallContent($"call_{i}", $"tool_{i}", new Dictionary<string, object?> { ["x"] = i }))
            .ToList();
        var fiveToolCalls = new ChatResponse(new ChatMessage(ChatRole.Assistant, fiveCalls));
        var clientFiveTools = new FastChatClient(fiveToolCalls);
        var runtimeFiveTools = new ChatClientRuntime(clientFiveTools, Options.Create(new ChatClientRuntimeOptions
        {
            MaxParallelToolCalls = 5,
            MaxToolRoundTrips = 2,
        }));
        var assemblyFive = new AgentAssembly();
        for (var i = 0; i < 5; i++)
        {
            assemblyFive.Tools.Add(new FastEchoTool($"tool_{i}"));
        }
        _fiveToolsAgent = runtimeFiveTools.CreateAgent(new AgentDefinition("five-tools-agent", []), assemblyFive);

        // 4. Streaming
        var clientStreaming = new FastChatClient(streamChunkCount: 50);
        var runtimeStreaming = new ChatClientRuntime(clientStreaming, Options.Create(new ChatClientRuntimeOptions()));
        _streamingAgent = runtimeStreaming.CreateAgent(new AgentDefinition("streaming-agent", []), new AgentAssembly());
    }

    [Benchmark]
    public async ValueTask<AgentTurnResult> InvokeAsync_SingleTurn_NoTools()
    {
        return await _noToolsAgent.InvokeAsync(_turnContext).ConfigureAwait(false);
    }

    [Benchmark]
    public async ValueTask<AgentTurnResult> InvokeAsync_SingleToolCall_RoundTrip()
    {
        return await _singleToolAgent.InvokeAsync(_turnContext).ConfigureAwait(false);
    }

    [Benchmark]
    public async ValueTask<AgentTurnResult> InvokeAsync_FiveParallelToolCalls_RoundTrip()
    {
        return await _fiveToolsAgent.InvokeAsync(_turnContext).ConfigureAwait(false);
    }

    [Benchmark]
    public async ValueTask<int> InvokeStreamingAsync_50TokenChunks_NoMiddleware()
    {
        var count = 0;
        await foreach (var chunk in _streamingAgent.InvokeStreamingAsync(_turnContext).ConfigureAwait(false))
        {
            count += chunk.Output.Count;
        }
        return count;
    }
}
