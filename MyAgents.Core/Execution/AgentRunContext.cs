namespace MyAgents.Core.Execution;

/// <summary>Execution metadata and checkpoint access supplied to a run handler.</summary>
public sealed class AgentRunContext
{
    private readonly Func<string, CancellationToken, ValueTask<bool>> _saveCheckpointAsync;

    /// <summary>The current run lease.</summary>
    public AgentRunLease Lease { get; }

    /// <summary>Stable run identifier.</summary>
    public string RunId => Lease.RunId;

    /// <summary>Handler identifier selected for this run.</summary>
    public string HandlerId => Lease.Request.HandlerId;

    /// <summary>Original versioned JSON input.</summary>
    public string InputJson => Lease.Request.InputJson;

    /// <summary>Most recently persisted handler checkpoint, if any.</summary>
    public string? CheckpointJson => Lease.CheckpointJson;

    /// <summary>JSON input supplied when a paused run was resumed, if any.</summary>
    public string? ResumeInputJson => Lease.ResumeInputJson;

    /// <summary>Current delivery attempt number, starting at one.</summary>
    public int Attempt => Lease.Attempt;

    /// <summary>Optional deadline for the run.</summary>
    public DateTimeOffset? Deadline => Lease.Request.Deadline;

    /// <summary>
    /// Creates a repeatable operation key for an external side effect. The supplied step ID
    /// must be stable across retries; the external system must enforce deduplication.
    /// </summary>
    public string CreateOperationId(string stableStepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableStepId);
        return $"{RunId}:{stableStepId}";
    }

    /// <summary>Persists a handler-defined checkpoint while this lease remains current.</summary>
    public ValueTask<bool> SaveCheckpointAsync(
        string checkpointJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpointJson);
        return _saveCheckpointAsync(checkpointJson, cancellationToken);
    }

    /// <summary>Creates a run context for the current lease.</summary>
    public AgentRunContext(
        AgentRunLease lease,
        Func<string, CancellationToken, ValueTask<bool>> saveCheckpointAsync)
    {
        Lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _saveCheckpointAsync = saveCheckpointAsync ?? throw new ArgumentNullException(nameof(saveCheckpointAsync));
    }
}
