using System.Runtime.CompilerServices;
using System.Text.Json;
using ComponentDescription = System.ComponentModel.DescriptionAttribute;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyAgents.Agents;
using MyAgents.Core;
using MyAgents.Core.Tools;
using MyAgents.Runtime.Chat;
using MyAgents.Testing;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class RuntimeAndDxUpgradeTests
{
    // =========================================================================
    // 1. DelegateTool & Tool Authoring Tests
    // =========================================================================

    [Test]
    public async Task DelegateTool_creates_schema_and_invokes_sync_function()
    {
        var tool = DelegateTool.Create(
            (string city, int days) => $"{city} for {days} days",
            name: "trip_planner",
            description: "Plans a trip");

        Assert.Multiple(() =>
        {
            Assert.That(tool.Name, Is.EqualTo("trip_planner"));
            Assert.That(tool.Description, Is.EqualTo("Plans a trip"));
            Assert.That(tool.ParametersSchemaJson, Does.Contain("city"));
            Assert.That(tool.ParametersSchemaJson, Does.Contain("days"));
            Assert.That(tool.ParametersSchemaJson, Does.Contain("required"));
        });

        var result = await tool.InvokeAsync("{\"city\":\"Tokyo\",\"days\":3}");
        Assert.That(result, Is.EqualTo("Tokyo for 3 days"));
    }

    [ComponentDescription("Fetches weather forecast")]
    private static string SampleWeatherTool(
        [ComponentDescription("Target city name")] string city,
        int count = 1)
        => $"{count} day forecast for {city}";

    [Test]
    public async Task DelegateTool_derives_name_and_descriptions_from_attributes_and_defaults()
    {
        var tool = DelegateTool.Create(SampleWeatherTool);

        Assert.Multiple(() =>
        {
            Assert.That(tool.Name, Is.EqualTo(nameof(SampleWeatherTool)));
            Assert.That(tool.Description, Is.EqualTo("Fetches weather forecast"));
            Assert.That(tool.ParametersSchemaJson, Does.Contain("Target city name"));
        });

        // Invoking with only required parameter; optional parameter uses default value
        var result = await tool.InvokeAsync("{\"city\":\"Montreal\"}");
        Assert.That(result, Is.EqualTo("1 day forecast for Montreal"));
    }

    [Test]
    public async Task DelegateTool_handles_async_tasks_and_cancellation_token()
    {
        var invoked = false;
        var tool = DelegateTool.Create(async (string message, CancellationToken ct) =>
        {
            await Task.Yield();
            invoked = true;
            ct.ThrowIfCancellationRequested();
            return $"echo: {message}";
        }, "async_echo");

        // CancellationToken must not be exposed in the parameter schema
        Assert.That(tool.ParametersSchemaJson, Does.Not.Contain("CancellationToken"));
        Assert.That(tool.ParametersSchemaJson, Does.Not.Contain("cancellationToken"));

        var result = await tool.InvokeAsync("{\"message\":\"hello\"}");
        Assert.Multiple(() =>
        {
            Assert.That(invoked, Is.True);
            Assert.That(result, Is.EqualTo("echo: hello"));
        });
    }

    [Test]
    public async Task DelegateTool_serializes_complex_return_types_to_json()
    {
        var tool = DelegateTool.Create((int a, int b) => ValueTask.FromResult(new
        {
            Sum = a + b,
            Product = a * b,
        }), "math_tool");

        var result = await tool.InvokeAsync("{\"a\":3,\"b\":4}");
        using var doc = JsonDocument.Parse(result);
        Assert.Multiple(() =>
        {
            Assert.That(doc.RootElement.GetProperty("Sum").GetInt32(), Is.EqualTo(7));
            Assert.That(doc.RootElement.GetProperty("Product").GetInt32(), Is.EqualTo(12));
        });
    }

    [Test]
    public void DelegateTool_throws_on_missing_required_arguments()
    {
        var tool = DelegateTool.Create((string requiredArg) => requiredArg, "strict_tool");

        var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
            await tool.InvokeAsync("{}"));

        Assert.That(ex!.Message, Does.Contain("Missing required argument 'requiredArg'"));
    }

    [Test]
    public void ToolsFeature_registers_functions_and_capabilities()
    {
        var feature = new ToolsFeature()
            .AddFunction((string query) => $"search: {query}", "search", "Searches web");

        var assembly = new AgentAssembly();
        feature.Contribute(assembly);

        Assert.Multiple(() =>
        {
            Assert.That(assembly.Tools.Build(), Has.Count.EqualTo(1));
            Assert.That(assembly.Tools.Build()[0].Name, Is.EqualTo("search"));
            Assert.That(assembly.Capabilities, Does.Contain(AgentCapabilities.Tools));
        });
    }

    // =========================================================================
    // 2. Strongly-Typed Structured Outputs Tests
    // =========================================================================

    public sealed record CityWeather(string City, int TemperatureC, string Condition);

    [Test]
    public void AgentResponseFormat_ForType_generates_valid_json_schema()
    {
        var format = AgentResponseFormat.ForType<CityWeather>("CityWeather", "Weather info");

        Assert.That(format, Is.InstanceOf<AgentResponseFormat.JsonSchemaFormat>());
        var schemaFormat = (AgentResponseFormat.JsonSchemaFormat)format;
        Assert.Multiple(() =>
        {
            Assert.That(schemaFormat.SchemaName, Is.EqualTo("CityWeather"));
            Assert.That(schemaFormat.SchemaDescription, Is.EqualTo("Weather info"));
            Assert.That(schemaFormat.SchemaJson, Does.Contain("City"));
            Assert.That(schemaFormat.SchemaJson, Does.Contain("TemperatureC"));
            Assert.That(schemaFormat.SchemaJson, Does.Contain("Condition"));
        });
    }

    [Test]
    public async Task Session_InvokeAsync_structured_deserializes_json_and_markdown_fences()
    {
        var json = "{\"City\":\"Ottawa\",\"TemperatureC\":-5,\"Condition\":\"Snowing\"}";
        var session = FakeAgentSession.Create("structured-session")
            .EnqueueResponse($"```json\n{json}\n```");

        var structured = await session.InvokeAsync<CityWeather>([AgentMessage.FromText("user", "Weather in Ottawa")]);

        Assert.Multiple(() =>
        {
            Assert.That(structured.Value.City, Is.EqualTo("Ottawa"));
            Assert.That(structured.Value.TemperatureC, Is.EqualTo(-5));
            Assert.That(structured.Value.Condition, Is.EqualTo("Snowing"));
            Assert.That(structured.TurnResult.Output.Count, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task StructuredOutputFeature_retries_on_invalid_json_and_succeeds()
    {
        var feature = new StructuredOutputFeature<CityWeather>(maxValidationRetries: 2);
        var assembly = new AgentAssembly();
        feature.Contribute(assembly);
        var pipeline = assembly.Pipeline.Build();

        var agent = new InMemoryAgent("retry-agent")
            .EnqueueResponse("Sorry, here is some bad json: { invalid }")
            .EnqueueResponse("{\"City\":\"Toronto\",\"TemperatureC\":20,\"Condition\":\"Clear\"}");

        using var provider = new ServiceCollection().BuildServiceProvider();
        var context = new AgentTurnContext(
            "retry-session",
            [AgentMessage.FromText("user", "Weather in Toronto")],
            new AgentTurnServices(provider));

        var result = await pipeline.InvokeAsync(agent, context);

        Assert.Multiple(() =>
        {
            Assert.That(agent.InvocationCount, Is.EqualTo(2));
            Assert.That(result.Output.Last().Content, Does.Contain("Toronto"));
        });
    }

    [Test]
    public void StructuredOutputFeature_fails_when_all_retries_exhausted()
    {
        var feature = new StructuredOutputFeature<CityWeather>(maxValidationRetries: 1);
        var assembly = new AgentAssembly();
        feature.Contribute(assembly);
        var pipeline = assembly.Pipeline.Build();

        var agent = new InMemoryAgent("fail-agent")
            .EnqueueResponse("bad json 1")
            .EnqueueResponse("bad json 2");

        using var provider = new ServiceCollection().BuildServiceProvider();
        var context = new AgentTurnContext(
            "fail-session",
            [AgentMessage.FromText("user", "Weather")],
            new AgentTurnServices(provider));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.InvokeAsync(agent, context));

        Assert.That(ex!.Message, Does.Contain("failed to produce valid JSON"));
    }

    // =========================================================================
    // 3. Streaming and Parallel Tool Execution Tests
    // =========================================================================

    [Test]
    public async Task ChatClientRuntime_forwards_ResponseFormat_to_ChatOptions()
    {
        var client = new CapturingChatClient();
        client.EnqueueResponse(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"City\":\"Calgary\",\"TemperatureC\":10,\"Condition\":\"Windy\"}")));

        using var provider = new ServiceCollection().BuildServiceProvider();
        var runtime = new ChatClientRuntime(client, Options.Create(new ChatClientRuntimeOptions()));
        var session = await new DefaultAgentSessionFactory(runtime, provider).CreateSessionAsync(
            "format-session",
            new AgentDefinition("format-agent", []));

        var structured = await session.InvokeAsync<CityWeather>([AgentMessage.FromText("user", "Calgary weather")]);

        Assert.Multiple(() =>
        {
            Assert.That(structured.Value.City, Is.EqualTo("Calgary"));
            Assert.That(client.LastOptions?.ResponseFormat, Is.Not.Null);
            Assert.That(client.LastOptions?.ResponseFormat, Is.InstanceOf<ChatResponseFormatJson>());
        });
    }

    [Test]
    public async Task ChatClientRuntime_InvokeStreamingAsync_streams_text_deltas()
    {
        var client = new CapturingChatClient();
        client.EnqueueStream([
            new ChatResponseUpdate(ChatRole.Assistant, "Hel"),
            new ChatResponseUpdate(null, "lo, "),
            new ChatResponseUpdate(null, "wor"),
            new ChatResponseUpdate(null, "ld!"),
        ]);

        using var provider = new ServiceCollection().BuildServiceProvider();
        var runtime = new ChatClientRuntime(client, Options.Create(new ChatClientRuntimeOptions()));
        var session = await new DefaultAgentSessionFactory(runtime, provider).CreateSessionAsync(
            "stream-session",
            new AgentDefinition("stream-agent", []));

        var chunks = new List<string>();
        await foreach (var result in session.InvokeStreamingAsync([AgentMessage.FromText("user", "say hello")]))
        {
            chunks.Add(result.Output[0].Content);
        }

        Assert.Multiple(() =>
        {
            Assert.That(chunks, Has.Count.EqualTo(4));
            Assert.That(string.Concat(chunks), Is.EqualTo("Hello, world!"));
        });
    }

    [Test]
    public async Task ChatClientRuntime_executes_parallel_tools_concurrently()
    {
        var tool1 = new DelayedTool("fast_tool", delayMs: 10, returnValue: "fast");
        var tool2 = new DelayedTool("slow_tool", delayMs: 50, returnValue: "slow");

        var client = new CapturingChatClient();
        client.EnqueueResponse(new ChatResponse(new ChatMessage(ChatRole.Assistant, [
            new FunctionCallContent("call-1", "fast_tool", new Dictionary<string, object?>()),
            new FunctionCallContent("call-2", "slow_tool", new Dictionary<string, object?>()),
        ])));
        client.EnqueueResponse(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Both finished")));

        using var provider = new ServiceCollection().BuildServiceProvider();
        var runtime = new ChatClientRuntime(client, Options.Create(new ChatClientRuntimeOptions
        {
            MaxParallelToolCalls = -1, // Parallel
        }));

        var session = await new DefaultAgentSessionFactory(runtime, provider).CreateSessionAsync(
            "parallel-session",
            new AgentDefinition("parallel-agent", [new ToolsFeature(tool1, tool2)]));

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "run tools")]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Output[0].Content, Is.EqualTo("Both finished"));
            Assert.That(result.Telemetry.ToolCalls, Is.EqualTo(2));
            Assert.That(tool1.CallCount, Is.EqualTo(1));
            Assert.That(tool2.CallCount, Is.EqualTo(1));

            // Verify both tool results were added to the second request in expected order
            var secondRequest = client.Requests[1];
            var toolResults = secondRequest.Where(m => m.Role == ChatRole.Tool).ToList();
            Assert.That(toolResults, Has.Count.EqualTo(2));
            Assert.That(toolResults[0].Contents.OfType<FunctionResultContent>().Single().CallId, Is.EqualTo("call-1"));
            Assert.That(toolResults[1].Contents.OfType<FunctionResultContent>().Single().CallId, Is.EqualTo("call-2"));
        });
    }

    // =========================================================================
    // Test Doubles & Helpers
    // =========================================================================

    private sealed class DelayedTool(string name, int delayMs, string returnValue) : ITool
    {
        public string Name => name;
        public string? Description => "A delayed tool";
        public int CallCount { get; private set; }

        public async ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
        {
            CallCount++;
            await Task.Delay(delayMs, cancellationToken);
            return returnValue;
        }
    }

    private sealed class CapturingChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new();
        private readonly Queue<IReadOnlyList<ChatResponseUpdate>> _streamingResponses = new();

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public ChatOptions? LastOptions { get; private set; }

        public void EnqueueResponse(ChatResponse response) => _responses.Enqueue(response);
        public void EnqueueStream(IReadOnlyList<ChatResponseUpdate> updates) => _streamingResponses.Enqueue(updates);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(messages.ToArray());
            LastOptions = options;
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
            Requests.Add(messages.ToArray());
            LastOptions = options;
            if (_streamingResponses.Count == 0)
                throw new InvalidOperationException("No fake streaming response is queued.");

            var updates = _streamingResponses.Dequeue();
            foreach (var update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }

        public void Dispose() { }
    }
}
