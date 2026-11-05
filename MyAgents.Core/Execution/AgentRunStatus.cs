namespace MyAgents.Core.Execution;

/// <summary>Durable lifecycle state for a submitted agent run.</summary>
public enum AgentRunStatus
{
    Queued,
    Leased,
    WaitingForInput,
    RetryScheduled,
    Succeeded,
    Failed,
    Cancelled,
    DeadlineExceeded,
}
