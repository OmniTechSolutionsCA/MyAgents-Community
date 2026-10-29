using System.Text.Json;
using MyAgents.Core.Tools;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

public class SampleTools
{
    [AgentTool(Description = "Performs addition of two integers")]
    public static int Add(
        [System.ComponentModel.Description("First integer operand")] int a,
        [System.ComponentModel.Description("Second integer operand")] int b)
    {
        return a + b;
    }

    [AgentTool(Name = "custom_multiply", Description = "Multiplies two numbers with an optional scale factor")]
    public double Multiply(
        double x,
        double y,
        double scale = 1.0)
    {
        return x * y * scale;
    }

    [AgentTool(Description = "Simulates async lookup with cancellation")]
    public async Task<string> FetchDataAsync(
        string query,
        bool verbose = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        return verbose ? $"Data for '{query}' (verbose)" : $"Data for '{query}'";
    }
}

[TestFixture]
public sealed class SourceGeneratedAgentToolTests
{
    [Test]
    public void Generated_static_tool_has_correct_metadata_and_schema()
    {
        var tool = SampleToolsAgentToolsExtensions.AsAddTool();

        Assert.That(tool.Name, Is.EqualTo("Add"));
        Assert.That(tool.Description, Is.EqualTo("Performs addition of two integers"));
        Assert.That(tool.ParametersSchemaJson, Is.Not.Null);

        using var doc = JsonDocument.Parse(tool.ParametersSchemaJson!);
        var root = doc.RootElement;
        Assert.That(root.GetProperty("type").GetString(), Is.EqualTo("object"));

        var props = root.GetProperty("properties");
        Assert.That(props.TryGetProperty("a", out var aProp), Is.True);
        Assert.That(aProp.GetProperty("type").GetString(), Is.EqualTo("integer"));
        Assert.That(aProp.GetProperty("description").GetString(), Is.EqualTo("First integer operand"));

        Assert.That(props.TryGetProperty("b", out var bProp), Is.True);
        Assert.That(bProp.GetProperty("type").GetString(), Is.EqualTo("integer"));
        Assert.That(bProp.GetProperty("description").GetString(), Is.EqualTo("Second integer operand"));

        var req = root.GetProperty("required");
        Assert.That(req.GetArrayLength(), Is.EqualTo(2));
    }

    [Test]
    public async Task Generated_static_tool_invokes_with_zero_boxing()
    {
        var tool = SampleToolsAgentToolsExtensions.AsAddTool();

        var result = await tool.InvokeAsync("{\"a\": 40, \"b\": 2}");
        Assert.That(result, Is.EqualTo("42"));
    }

    [Test]
    public async Task Generated_instance_tool_supports_custom_name_and_defaults()
    {
        var target = new SampleTools();
        var tool = target.AsMultiplyTool();

        Assert.That(tool.Name, Is.EqualTo("custom_multiply"));
        Assert.That(tool.Description, Is.EqualTo("Multiplies two numbers with an optional scale factor"));

        // Default scale = 1.0
        var result1 = await tool.InvokeAsync("{\"x\": 3.5, \"y\": 2.0}");
        Assert.That(result1, Is.EqualTo("7"));

        // Explicit scale = 10.0
        var result2 = await tool.InvokeAsync("{\"x\": 3.5, \"y\": 2.0, \"scale\": 10.0}");
        Assert.That(result2, Is.EqualTo("70"));
    }

    [Test]
    public async Task Generated_async_tool_handles_cancellation_and_bool_args()
    {
        var target = new SampleTools();
        var tool = target.AsFetchDataAsyncTool();

        var normalResult = await tool.InvokeAsync("{\"query\": \"agent_state\", \"verbose\": true}");
        Assert.That(normalResult, Is.EqualTo("Data for 'agent_state' (verbose)"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await tool.InvokeAsync("{\"query\": \"cancelled_op\"}", cts.Token);
        });
    }

    [Test]
    public void AsAgentTools_returns_all_tools_for_instance()
    {
        var target = new SampleTools();
        var tools = target.AsAgentTools();

        Assert.That(tools.Count, Is.EqualTo(3));
        Assert.That(tools.Select(t => t.Name), Is.EquivalentTo(new[] { "Add", "custom_multiply", "FetchDataAsync" }));
    }
}
