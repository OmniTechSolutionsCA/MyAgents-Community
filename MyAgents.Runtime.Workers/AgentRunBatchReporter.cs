using MyAgents.Core.Execution;
using MyAgents.Core;

namespace MyAgents.Runtime.Workers;

/// <summary>Builds batch outcome summaries from backend run records.</summary>
public sealed class AgentRunBatchReporter
{
    private readonly IAgentRunBatchCatalog _catalog;
    private readonly IRunTraceSink? _traceSink;

    /// <summary>Creates a reporter using a backend that supports batch queries.</summary>
    public AgentRunBatchReporter(IAgentRunBatchCatalog catalog)
        : this(catalog, traceSink: null)
    {
    }

    /// <summary>Creates a reporter with an optional trace reader for payload-free event summaries.</summary>
    public AgentRunBatchReporter(IAgentRunBatchCatalog catalog, IRunTraceSink? traceSink)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _traceSink = traceSink;
    }

    /// <summary>Loads and summarizes current outcomes for a batch.</summary>
    public async ValueTask<AgentRunBatchSummary> GetSummaryAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        var runs = await _catalog.GetBatchRunsAsync(batchId, cancellationToken).ConfigureAwait(false);
        var succeeded = runs.Count(static run => run.Status == AgentRunStatus.Succeeded);
        var failed = runs.Count(static run => run.Status == AgentRunStatus.Failed);
        var cancelled = runs.Count(static run => run.Status == AgentRunStatus.Cancelled);
        var deadlineExceeded = runs.Count(static run => run.Status == AgentRunStatus.DeadlineExceeded);
        var pending = runs.Count - succeeded - failed - cancelled - deadlineExceeded;
        var traces = _traceSink is null
            ? null
            : await CreateTraceSummariesAsync(runs, cancellationToken).ConfigureAwait(false);
        return new AgentRunBatchSummary(batchId, runs, succeeded, failed, cancelled, deadlineExceeded, pending, traces);
    }

    private async ValueTask<IReadOnlyList<AgentRunTraceSummary>> CreateTraceSummariesAsync(
        IReadOnlyList<AgentRunRecord> runs,
        CancellationToken cancellationToken)
    {
        var summaries = new List<AgentRunTraceSummary>(runs.Count);
        foreach (var run in runs)
        {
            var records = await _traceSink!.ListAsync(run.RunId, cancellationToken).ConfigureAwait(false);
            var counts = records
                .GroupBy(static record => record.EventKind)
                .ToDictionary(static group => group.Key, static group => group.Count());
            summaries.Add(new AgentRunTraceSummary(run.RunId, records.Count, counts));
        }

        return summaries;
    }
}
