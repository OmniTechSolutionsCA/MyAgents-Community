using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyAgents.Core.Execution;
using MyAgents.Runtime.Workers;
using MyAgents.Testing;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class AgentRunWorkerConcurrencyTests
{
    [Test]
    public async Task Worker_processes_runs_up_to_configured_concurrency()
    {
        var backend = new InMemoryAgentRunBackend();
        for (var index = 0; index < 3; index++)
            await backend.SubmitAsync(new AgentRunRequest("blocking", "{}"));

        var handler = new BlockingHandler();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IAgentRunBackend>(backend);
        builder.Services.AddSingleton<IAgentRunHandler>(handler);
        builder.Services.AddMyAgentsRunWorker(options =>
        {
            options.MaxConcurrentRuns = 2;
            options.IdlePollInterval = TimeSpan.FromMilliseconds(20);
            options.LeaseDuration = TimeSpan.FromSeconds(30);
            options.LeaseRenewalInterval = TimeSpan.FromSeconds(10);
        });

        using var host = builder.Build();
        await host.StartAsync();
        await handler.TwoRunsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(handler.StartedCount, Is.EqualTo(2));

        handler.Release.TrySetResult();
        await handler.AllRunsFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();
        Assert.That(handler.MaximumActiveCount, Is.LessThanOrEqualTo(2));
    }

    private sealed class BlockingHandler : IAgentRunHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _twoRunsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allRunsFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startedCount;
        private int _activeCount;
        private int _maximumActiveCount;
        private int _finishedCount;

        public string HandlerId => "blocking";
        public int StartedCount => Volatile.Read(ref _startedCount);
        public int MaximumActiveCount => Volatile.Read(ref _maximumActiveCount);
        public TaskCompletionSource TwoRunsStarted => _twoRunsStarted;
        public TaskCompletionSource AllRunsFinished => _allRunsFinished;
        public TaskCompletionSource Release => _release;

        public async ValueTask<AgentRunHandlerResult> ExecuteAsync(
            AgentRunContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _startedCount);
            var active = Interlocked.Increment(ref _activeCount);
            UpdateMaximumActiveCount(active);
            if (active == 2)
                _twoRunsStarted.TrySetResult();

            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Decrement(ref _activeCount);
            if (Interlocked.Increment(ref _finishedCount) == 3)
                _allRunsFinished.TrySetResult();
            return AgentRunHandlerResult.Completed();
        }

        private void UpdateMaximumActiveCount(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumActiveCount);
                if (active <= current || Interlocked.CompareExchange(ref _maximumActiveCount, active, current) == current)
                    return;
            }
        }
    }
}
