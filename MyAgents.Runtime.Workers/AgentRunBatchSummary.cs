using MyAgents.Core.Execution;

namespace MyAgents.Runtime.Workers;

/// <summary>Aggregated run outcomes for one batch.</summary>
public sealed record AgentRunBatchSummary(
    string BatchId,
    IReadOnlyList<AgentRunRecord> Runs,
    int SucceededCount,
    int FailedCount,
    int CancelledCount,
    int DeadlineExceededCount,
    int PendingCount,
    IReadOnlyList<AgentRunTraceSummary>? TraceSummaries = null)
{
    /// <summary>Number of cases recorded for this batch.</summary>
    public int RunCount => Runs.Count;

    /// <summary>Whether every case reached a terminal state.</summary>
    public bool IsComplete => PendingCount == 0;

    /// <summary>Fraction of all cases that succeeded, or null for an empty batch.</summary>
    public double? SuccessRate => RunCount == 0 ? null : (double)SucceededCount / RunCount;
}
