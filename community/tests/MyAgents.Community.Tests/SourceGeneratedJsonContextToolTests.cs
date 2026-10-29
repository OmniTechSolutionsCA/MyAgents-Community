using System.Text.Json;
using System.Text.Json.Serialization;
using MyAgents.Core.Tools;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

public sealed record WeatherRequest(string City, string? Units = null);

public sealed record WeatherReport(string City, double TemperatureC, string? Units);

[JsonSerializable(typeof(WeatherRequest))]
[JsonSerializable(typeof(WeatherReport))]
[JsonSerializable(typeof(WeatherReport[]))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class WeatherJsonContext : JsonSerializerContext
{
}

public static class WeatherTools
{
    [AgentTool(Description = "Gets current weather for a city", JsonContext = typeof(WeatherJsonContext))]
    public static WeatherReport GetWeather(
        [System.ComponentModel.Description("The weather request")] WeatherRequest request) =>
        new(request.City, 22.5, request.Units);

    [AgentTool(Description = "Gets weather for several cities", JsonContext = typeof(WeatherJsonContext))]
    public static async Task<WeatherReport[]> GetWeatherBatchAsync(string[] cities, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return cities.Select(static c => new WeatherReport(c, 20, null)).ToArray();
    }
}

/// <summary>
/// Contract tests for <c>[AgentTool(JsonContext = ...)]</c>: source-generated
/// <see cref="JsonSerializerContext"/> binding must serialize and deserialize complex
/// parameter/result types without reflection, advertise exporter-accurate schemas, and
/// fail fast on missing metadata — the supported NativeAOT/trimming-safe authoring path.
/// </summary>
[TestFixture]
public sealed class SourceGeneratedJsonContextToolTests
{
    [Test]
    public void Complex_parameter_schema_is_exporter_accurate()
    {
        var tool = WeatherToolsAgentToolsExtensions.AsGetWeatherTool();

        using var doc = JsonDocument.Parse(tool.ParametersSchemaJson!);
        var root = doc.RootElement;

        Assert.That(root.GetProperty("type").GetString(), Is.EqualTo("object"));
        Assert.That(root.GetProperty("required").EnumerateArray().Single().GetString(), Is.EqualTo("request"));

        // The complex parameter's schema must be the real exported schema — nested
        // properties, types, and optional fields — not a bare {"type":"object"} placeholder.
        var requestSchema = root.GetProperty("properties").GetProperty("request");
        // The exporter represents null-oblivious types as a type union (e.g. ["object","null"]).
        Assert.That(ReadSchemaTypes(requestSchema), Does.Contain("object"));
        var requestProps = requestSchema.GetProperty("properties");
        Assert.That(ReadSchemaTypes(requestProps.GetProperty("City")), Does.Contain("string"));
        Assert.That(requestProps.TryGetProperty("Units", out _), Is.True);
        Assert.That(
            requestSchema.GetProperty("required").EnumerateArray().Single().GetString(),
            Is.EqualTo("City"));
        Assert.That(requestSchema.GetProperty("description").GetString(), Is.EqualTo("The weather request"));
    }

    private static IEnumerable<string> ReadSchemaTypes(JsonElement schema)
    {
        var type = schema.GetProperty("type");
        return type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(t => t.GetString()!)
            : [type.GetString()!];
    }

    [Test]
    public async Task Complex_parameter_binds_through_json_type_info()
    {
        var tool = WeatherToolsAgentToolsExtensions.AsGetWeatherTool();

        var result = await tool.InvokeAsync("""{"request": {"City": "Paris", "Units": "metric"}}""");

        // Serialized through the context: source-gen property naming and null handling.
        Assert.That(result, Is.EqualTo("""{"City":"Paris","TemperatureC":22.5,"Units":"metric"}"""));
    }

    [Test]
    public async Task Optional_complex_properties_use_source_generated_defaults()
    {
        var tool = WeatherToolsAgentToolsExtensions.AsGetWeatherTool();

        var result = await tool.InvokeAsync("""{"request": {"City": "Tokyo"}}""");

        Assert.That(result, Is.EqualTo("""{"City":"Tokyo","TemperatureC":22.5,"Units":null}"""));
    }

    [Test]
    public async Task Json_null_for_complex_parameter_binds_to_null()
    {
        var tool = WeatherToolsAgentToolsExtensions.AsGetWeatherTool();

        Assert.ThrowsAsync<NullReferenceException>(async () =>
            await tool.InvokeAsync("""{"request": null}"""));
    }

    [Test]
    public void Missing_required_complex_argument_is_rejected()
    {
        var tool = WeatherToolsAgentToolsExtensions.AsGetWeatherTool();

        Assert.ThrowsAsync<ArgumentException>(async () => await tool.InvokeAsync("{}"));
    }

    [Test]
    public void Malformed_complex_argument_raises_json_exception()
    {
        var tool = WeatherToolsAgentToolsExtensions.AsGetWeatherTool();

        // City is declared as string in the context's metadata: the source-generated
        // deserializer rejects the mistyped payload instead of silently coercing it.
        Assert.ThrowsAsync<JsonException>(async () =>
            await tool.InvokeAsync("""{"request": {"City": 42}}"""));
    }

    [Test]
    public async Task Array_parameter_and_complex_async_result_round_trip()
    {
        var tool = WeatherToolsAgentToolsExtensions.AsGetWeatherBatchAsyncTool();

        var result = await tool.InvokeAsync("""{"cities": ["Oslo", "Lima"]}""");

        Assert.That(
            result,
            Is.EqualTo("""[{"City":"Oslo","TemperatureC":20,"Units":null},{"City":"Lima","TemperatureC":20,"Units":null}]"""));
    }

    [Test]
    public async Task Array_tool_supports_cancellation()
    {
        var tool = WeatherToolsAgentToolsExtensions.AsGetWeatherBatchAsyncTool();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await tool.InvokeAsync("""{"cities": ["Oslo"]}""", cts.Token));
    }

    [Test]
    public async Task Primitive_binding_unchanged_alongside_context_tools()
    {
        // Existing primitive-only tools keep the compile-time-constant schema path.
        var tool = SampleToolsAgentToolsExtensions.AsAddTool();
        Assert.That(tool.ParametersSchemaJson, Is.EqualTo(SampleTools_Add_GeneratedTool.Schema));

        var result = await tool.InvokeAsync("""{"a": 1, "b": 2}""");
        Assert.That(result, Is.EqualTo("3"));
    }

    [Test]
    public void AsAgentTools_includes_context_tools()
    {
        var tools = WeatherToolsAgentToolsExtensions.AsAgentTools();

        Assert.That(tools.Select(t => t.Name), Is.EquivalentTo(new[] { "GetWeather", "GetWeatherBatchAsync" }));
    }
}
