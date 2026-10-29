using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using MyAgents.Core;

namespace MyAgents.Benchmarks.Runtime;

[MemoryDiagnoser]
public class AgentPipelineBenchmarks
{
    private sealed class PassthroughMiddleware : IAgentMiddleware
    {
        public ValueTask<AgentTurnResult> InvokeAsync(
            AgentTurnContext context,
            Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>> next,
            CancellationToken cancellationToken = default)
        {
            return next(context, cancellationToken);
        }

        public IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
            AgentTurnContext context,
            Func<AgentTurnContext, CancellationToken, IAsyncEnumerable<AgentTurnResult>> next,
            CancellationToken cancellationToken = default)
        {
            return next(context, cancellationToken);
        }
    }

    private sealed class FastInnerAgent : IAgent
    {
        private static readonly AgentTurnResult ConstantResult = new(
            [AgentMessage.FromText("assistant", "Hello")],
            new AgentTurnTelemetry(TimeSpan.Zero));

        private static readonly AgentTurnResult[] ConstantStream = Enumerable.Repeat(ConstantResult, 5).ToArray();

        public ValueTask<AgentTurnResult> InvokeAsync(AgentTurnContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ConstantResult);

        public async IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
            AgentTurnContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < ConstantStream.Length; i++)
            {
                yield return ConstantStream[i];
            }
        }

        public void Dispose() { }
    }

    private readonly FastInnerAgent _agent = new();
    private AgentPipeline _pipeline = null!;
    private Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>> _cachedChain = null!;
    private Func<AgentTurnContext, CancellationToken, IAsyncEnumerable<AgentTurnResult>> _cachedStreamingChain = null!;
    private AgentTurnContext _context = null!;

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var turnServices = new AgentTurnServices(services);
        _context = new AgentTurnContext(
            "bench-session",
            [AgentMessage.FromText("user", "Run pipeline")],
            turnServices);

        var builder = new PipelineBuilder();
        for (var i = 0; i < 5; i++)
        {
            builder.Use(new PassthroughMiddleware());
        }

        _pipeline = builder.Build();
        _cachedChain = _pipeline.BuildChain(_agent);
        _cachedStreamingChain = _pipeline.BuildStreamingChain(_agent);
    }

    [Benchmark(Baseline = true)]
    public async ValueTask<AgentTurnResult> Pipeline_InvokeAsync_Uncached()
    {
        return await _pipeline.InvokeAsync(_agent, _context).ConfigureAwait(false);
    }

    [Benchmark]
    public async ValueTask<AgentTurnResult> Pipeline_InvokeAsync_CachedChain()
    {
        return await _cachedChain(_context, default).ConfigureAwait(false);
    }

    [Benchmark]
    public async ValueTask<int> Pipeline_InvokeStreamingAsync_5Stages()
    {
        var count = 0;
        await foreach (var chunk in _pipeline.InvokeStreamingAsync(_agent, _context).ConfigureAwait(false))
        {
            count += chunk.Output.Count;
        }
        return count;
    }
}
