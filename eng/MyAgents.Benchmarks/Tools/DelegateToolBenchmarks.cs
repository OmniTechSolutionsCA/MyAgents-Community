using System.Text.Json;
using BenchmarkDotNet.Attributes;
using MyAgents.Core.Tools;

namespace MyAgents.Benchmarks.Tools;

[MemoryDiagnoser]
public class DelegateToolBenchmarks
{
    private sealed class HandwrittenTool : ITool
    {
        public string Name => "add";
        public string? Description => "Adds two integers";
        public string? ParametersSchemaJson => "{\"type\":\"object\"}";

        public ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
        {
            using var doc = JsonDocument.Parse(jsonArguments);
            var root = doc.RootElement;
            var a = root.GetProperty("a").GetInt32();
            var b = root.GetProperty("b").GetInt32();
            _ = root.GetProperty("flag").GetBoolean();
            return ValueTask.FromResult((a + b).ToString());
        }
    }

    public sealed record WeatherReport(string City, double TemperatureC, string Condition);

    private readonly ITool _handwrittenTool = new HandwrittenTool();
    private readonly ITool _delegatePrimitiveTool;
    private readonly ITool _delegateAsyncTool;
    private readonly string _primitiveJsonArgs = "{\"a\":42,\"b\":108,\"flag\":true}";
    private readonly string _asyncJsonArgs = "{\"city\":\"Montreal\"}";
    private readonly Func<int, int, bool, CancellationToken, int> _addFunc = (a, b, flag, ct) => a + b;

    public DelegateToolBenchmarks()
    {
        _delegatePrimitiveTool = DelegateTool.Create(_addFunc, "add");
        _delegateAsyncTool = DelegateTool.Create(
            (string city, CancellationToken ct) => ValueTask.FromResult(new WeatherReport(city, 22.5, "Sunny")),
            "get_weather");
    }

    [Benchmark(Baseline = true)]
    public async ValueTask<string> HandwrittenITool_PrimitiveArgs()
    {
        return await _handwrittenTool.InvokeAsync(_primitiveJsonArgs).ConfigureAwait(false);
    }

    [Benchmark]
    public async ValueTask<string> DelegateTool_PrimitiveArgs()
    {
        return await _delegatePrimitiveTool.InvokeAsync(_primitiveJsonArgs).ConfigureAwait(false);
    }

    [Benchmark]
    public async ValueTask<string> DelegateTool_AsyncValueTask()
    {
        return await _delegateAsyncTool.InvokeAsync(_asyncJsonArgs).ConfigureAwait(false);
    }

    [Benchmark]
    public DelegateTool DelegateTool_Creation()
    {
        return DelegateTool.Create(_addFunc, "add");
    }
}
