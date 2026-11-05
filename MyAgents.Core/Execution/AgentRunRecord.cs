namespace MyAgents.Core.Execution;

/// <summary>Current durable state and outcome for a logical agent run.</summary>
public sealed record AgentRunRecord(
    string RunId,
    AgentRunRequest Request,
    AgentRunStatus Status,
    int Attempt,
    int FailureCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? NextAttemptAt = null,
    string? OutputJson = null,
    string? FailureCode = null,
    string? FailureMessage = null,
    string? CheckpointJson = null,
    string? ResumeInputJson = null,
    string? PendingInputDescription = null,
    string? WorkerId = null,
    string? LeaseId = null,
    long? FencingToken = null,
    DateTimeOffset? LeaseExpiresAt = null,
    bool CancellationRequested = false);
