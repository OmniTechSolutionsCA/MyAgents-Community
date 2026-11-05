namespace MyAgents.Core.Execution;

/// <summary>A worker's time-limited claim on a run.</summary>
public sealed record AgentRunLease(
    string RunId,
    AgentRunRequest Request,
    string WorkerId,
    string LeaseId,
    long FencingToken,
    int Attempt,
    int FailureCount,
    DateTimeOffset LeaseExpiresAt,
    string? CheckpointJson = null,
    string? ResumeInputJson = null,
    bool CancellationRequested = false);
