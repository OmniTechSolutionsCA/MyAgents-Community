namespace MyAgents.Core.Execution;

/// <summary>
/// Durable lifecycle and queue boundary for agent runs. Implementations must make submission,
/// claim, lease, and terminal transitions atomic within their own persistence boundary.
/// </summary>
/// <remarks>
/// Delivery is at-least-once. Implementations should use fencing tokens to reject updates from
/// workers whose lease has expired or been replaced. This contract does not provide exactly-once
/// external side effects.
/// </remarks>
public interface IAgentRunBackend
{
    /// <summary>Submits a run. Matching scoped idempotency keys return the existing run.</summary>
    ValueTask<AgentRunSubmission> SubmitAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the latest durable state of a run, or null when the ID is unknown.</summary>
    ValueTask<AgentRunRecord?> GetAsync(
        string runId,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically claims the next eligible run for a worker.</summary>
    ValueTask<AgentRunLease?> ClaimAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>Renews a current lease or reports that it expired, was replaced, or was cancelled.</summary>
    ValueTask<AgentRunLeaseRenewal> RenewLeaseAsync(
        AgentRunLease lease,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>Saves handler state only when the supplied lease is still current.</summary>
    ValueTask<bool> SaveCheckpointAsync(
        AgentRunLease lease,
        string checkpointJson,
        CancellationToken cancellationToken = default);

    /// <summary>Completes the run under the current lease.</summary>
    ValueTask<bool> CompleteAsync(
        AgentRunLease lease,
        string? outputJson = null,
        CancellationToken cancellationToken = default);

    /// <summary>Pauses the run until application input or an approval arrives.</summary>
    ValueTask<bool> WaitForInputAsync(
        AgentRunLease lease,
        string checkpointJson,
        string pendingInputDescription,
        CancellationToken cancellationToken = default);

    /// <summary>Resumes a run that is waiting for input and makes it eligible for claiming.</summary>
    ValueTask<bool> ResumeAsync(
        string runId,
        string resumeInputJson,
        CancellationToken cancellationToken = default);

    /// <summary>Schedules a retry or makes the run terminal when retry policy is exhausted.</summary>
    ValueTask<bool> RetryAsync(
        AgentRunLease lease,
        AgentRunFailure failure,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default);

    /// <summary>Marks a current run as failed, cancelled, or deadline-exceeded as applicable.</summary>
    ValueTask<bool> FailAsync(
        AgentRunLease lease,
        AgentRunFailure failure,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels an unleased run or requests cancellation from its current worker.</summary>
    ValueTask<bool> CancelAsync(
        string runId,
        CancellationToken cancellationToken = default);
}
