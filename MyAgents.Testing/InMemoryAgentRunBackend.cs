using MyAgents.Core.Execution;

namespace MyAgents.Testing;

/// <summary>
/// Single-process implementation of <see cref="IAgentRunBackend"/> for deterministic tests
/// and local development. It does not survive process exit or coordinate separate processes.
/// </summary>
public sealed class InMemoryAgentRunBackend : IAgentRunBackend, IAgentRunBatchCatalog, IAgentRunBatchController
{
    private readonly object _sync = new();
    private readonly Dictionary<string, MutableRun> _runs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _idempotencyKeys = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly AgentRunBackendOptions _options;

    /// <summary>Creates an in-memory backend using the system clock unless a test clock is supplied.</summary>
    public InMemoryAgentRunBackend(TimeProvider? timeProvider = null)
        : this(timeProvider, options: null)
    {
    }

    /// <summary>Creates an in-memory backend with optional per-scope limits.</summary>
    public InMemoryAgentRunBackend(TimeProvider? timeProvider, AgentRunBackendOptions? options)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new AgentRunBackendOptions();
        _options.Validate();
    }

    /// <inheritdoc />
    public ValueTask<AgentRunSubmission> SubmitAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);

        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            var idempotencyKey = GetIdempotencyKey(request);
            if (idempotencyKey is not null && _idempotencyKeys.TryGetValue(idempotencyKey, out var existingRunId))
            {
                var existing = _runs[existingRunId];
                if (!SameIdempotentRequest(existing.Request, request))
                {
                    throw new InvalidOperationException(
                        "The idempotency key is already associated with a different run request.");
                }

                return ValueTask.FromResult(new AgentRunSubmission(existingRunId, WasDeduplicated: true));
            }

            if (_options.MaxOutstandingRunsPerScope is { } limit &&
                _runs.Values.Count(run => SameScope(run.Request.ScopeKey, request.ScopeKey) && !IsTerminal(run.Status)) >= limit)
            {
                throw new AgentRunAdmissionRejectedException(request.ScopeKey, limit);
            }

            var runId = Guid.NewGuid().ToString("N");
            var run = new MutableRun(runId, request, now);
            _runs.Add(runId, run);
            if (idempotencyKey is not null)
                _idempotencyKeys.Add(idempotencyKey, runId);

            return ValueTask.FromResult(new AgentRunSubmission(runId, WasDeduplicated: false));
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AgentRunRecord>> GetBatchRunsAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);

        lock (_sync)
        {
            IReadOnlyList<AgentRunRecord> records = _runs.Values
                .Where(run => string.Equals(run.Request.BatchId, batchId, StringComparison.Ordinal))
                .OrderBy(static run => run.CreatedAt)
                .ThenBy(static run => run.RunId, StringComparer.Ordinal)
                .Select(static run => run.ToRecord())
                .ToArray();
            return ValueTask.FromResult(records);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> CancelBatchAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);

        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            var affected = 0;
            foreach (var run in _runs.Values)
            {
                if (!string.Equals(run.Request.BatchId, batchId, StringComparison.Ordinal) || IsTerminal(run.Status))
                    continue;

                if (run.Status == AgentRunStatus.Leased)
                {
                    run.CancellationRequested = true;
                    run.UpdatedAt = now;
                }
                else
                {
                    SetTerminal(run, AgentRunStatus.Cancelled, null, now);
                }

                affected++;
            }

            return ValueTask.FromResult(affected);
        }
    }

    /// <inheritdoc />
    public ValueTask<AgentRunRecord?> GetAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        lock (_sync)
        {
            return ValueTask.FromResult(
                _runs.TryGetValue(runId, out var run) ? run.ToRecord() : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<AgentRunLease?> ClaimAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);

        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            foreach (var run in _runs.Values)
            {
                if (run.Status == AgentRunStatus.Leased && run.LeaseExpiresAt <= now)
                    ExpireLease(run, now);

                if (run.Status != AgentRunStatus.Leased &&
                    !IsTerminal(run.Status) &&
                    IsPastDeadline(run, now))
                {
                    SetTerminal(run, AgentRunStatus.DeadlineExceeded, "The run deadline elapsed before execution.", now);
                }

                if (run.Status == AgentRunStatus.RetryScheduled && run.NextAttemptAt <= now)
                {
                    run.Status = AgentRunStatus.Queued;
                    run.NextAttemptAt = null;
                    run.UpdatedAt = now;
                }
            }

            var candidate = _runs.Values
                .Where(static run => run.Status == AgentRunStatus.Queued)
                .Where(IsBelowMaxLeasedRunsPerScope)
                .OrderBy(static run => run.CreatedAt)
                .FirstOrDefault();

            if (candidate is null)
                return ValueTask.FromResult<AgentRunLease?>(null);

            candidate.Status = AgentRunStatus.Leased;
            candidate.Attempt++;
            candidate.WorkerId = workerId;
            candidate.LeaseId = Guid.NewGuid().ToString("N");
            candidate.FencingToken++;
            candidate.LeaseExpiresAt = now + leaseDuration;
            candidate.CancellationRequested = false;
            candidate.UpdatedAt = now;

            return ValueTask.FromResult<AgentRunLease?>(candidate.ToLease());
        }
    }

    /// <inheritdoc />
    public ValueTask<AgentRunLeaseRenewal> RenewLeaseAsync(
        AgentRunLease lease,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);

        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            if (!TryGetCurrentLease(lease, now, out var run))
                return ValueTask.FromResult(new AgentRunLeaseRenewal(false, false));

            if (IsPastDeadline(run, now))
            {
                SetTerminal(run, AgentRunStatus.DeadlineExceeded, "The run deadline elapsed during execution.", now);
                return ValueTask.FromResult(new AgentRunLeaseRenewal(false, false));
            }

            run.LeaseExpiresAt = now + leaseDuration;
            run.UpdatedAt = now;
            return ValueTask.FromResult(new AgentRunLeaseRenewal(
                LeaseIsValid: true,
                CancellationRequested: run.CancellationRequested,
                LeaseExpiresAt: run.LeaseExpiresAt));
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> SaveCheckpointAsync(
        AgentRunLease lease,
        string checkpointJson,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(checkpointJson);

        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            if (!TryGetCurrentLease(lease, now, out var run))
                return ValueTask.FromResult(false);

            if (IsPastDeadline(run, now) || run.CancellationRequested)
                return ValueTask.FromResult(false);

            run.CheckpointJson = checkpointJson;
            run.UpdatedAt = now;
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> CompleteAsync(
        AgentRunLease lease,
        string? outputJson = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(lease);

        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            if (!TryGetCurrentLease(lease, now, out var run))
                return ValueTask.FromResult(false);

            if (run.CancellationRequested)
                SetTerminal(run, AgentRunStatus.Cancelled, null, now);
            else if (IsPastDeadline(run, now))
                SetTerminal(run, AgentRunStatus.DeadlineExceeded, "The run deadline elapsed during execution.", now);
            else
            {
                run.Status = AgentRunStatus.Succeeded;
                run.OutputJson = outputJson;
                run.UpdatedAt = now;
                ClearLease(run);
            }

            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> WaitForInputAsync(
        AgentRunLease lease,
        string checkpointJson,
        string pendingInputDescription,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(checkpointJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingInputDescription);

        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            if (!TryGetCurrentLease(lease, now, out var run))
                return ValueTask.FromResult(false);

            if (run.CancellationRequested)
                SetTerminal(run, AgentRunStatus.Cancelled, null, now);
            else if (IsPastDeadline(run, now))
                SetTerminal(run, AgentRunStatus.DeadlineExceeded, "The run deadline elapsed during execution.", now);
            else
            {
                run.Status = AgentRunStatus.WaitingForInput;
                run.CheckpointJson = checkpointJson;
                run.ResumeInputJson = null;
                run.PendingInputDescription = pendingInputDescription;
                run.UpdatedAt = now;
                ClearLease(run);
            }

            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> ResumeAsync(
        string runId,
        string resumeInputJson,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(resumeInputJson);

        lock (_sync)
        {
            if (!_runs.TryGetValue(runId, out var run) || run.Status != AgentRunStatus.WaitingForInput)
                return ValueTask.FromResult(false);

            var now = _timeProvider.GetUtcNow();
            if (IsPastDeadline(run, now))
            {
                SetTerminal(run, AgentRunStatus.DeadlineExceeded, "The run deadline elapsed while waiting for input.", now);
                return ValueTask.FromResult(false);
            }

            run.ResumeInputJson = resumeInputJson;
            run.PendingInputDescription = null;
            run.Status = AgentRunStatus.Queued;
            run.UpdatedAt = now;
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> RetryAsync(
        AgentRunLease lease,
        AgentRunFailure failure,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(lease);
        ValidateFailure(failure);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryDelay, TimeSpan.Zero);

        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            if (!TryGetCurrentLease(lease, now, out var run))
                return ValueTask.FromResult(false);

            if (run.CancellationRequested)
                SetTerminal(run, AgentRunStatus.Cancelled, null, now);
            else if (IsPastDeadline(run, now))
                SetTerminal(run, AgentRunStatus.DeadlineExceeded, "The run deadline elapsed during execution.", now);
            else
            {
                run.FailureCount++;
                run.FailureCode = failure.Code;
                run.FailureMessage = failure.Message;
                if (!failure.Retryable || run.FailureCount >= run.Request.MaxAttempts)
                {
                    SetTerminal(run, AgentRunStatus.Failed, failure.Message, now);
                    run.FailureCode = failure.Code;
                }
                else
                {
                    run.Status = AgentRunStatus.RetryScheduled;
                    run.NextAttemptAt = now + retryDelay;
                    run.UpdatedAt = now;
                    ClearLease(run);
                }
            }

            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> FailAsync(
        AgentRunLease lease,
        AgentRunFailure failure,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(lease);
        ValidateFailure(failure);

        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            if (!TryGetCurrentLease(lease, now, out var run))
                return ValueTask.FromResult(false);

            if (run.CancellationRequested)
                SetTerminal(run, AgentRunStatus.Cancelled, null, now);
            else if (IsPastDeadline(run, now))
                SetTerminal(run, AgentRunStatus.DeadlineExceeded, failure.Message, now);
            else
            {
                run.FailureCode = failure.Code;
                SetTerminal(run, AgentRunStatus.Failed, failure.Message, now);
                run.FailureCode = failure.Code;
            }

            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> CancelAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        lock (_sync)
        {
            if (!_runs.TryGetValue(runId, out var run) || IsTerminal(run.Status))
                return ValueTask.FromResult(false);

            var now = _timeProvider.GetUtcNow();
            if (run.Status == AgentRunStatus.Leased)
            {
                run.CancellationRequested = true;
                run.UpdatedAt = now;
            }
            else
            {
                SetTerminal(run, AgentRunStatus.Cancelled, null, now);
            }

            return ValueTask.FromResult(true);
        }
    }

    private bool TryGetCurrentLease(AgentRunLease lease, DateTimeOffset now, out MutableRun run)
    {
        if (_runs.TryGetValue(lease.RunId, out run!) &&
            run.Status == AgentRunStatus.Leased &&
            run.LeaseExpiresAt > now &&
            run.LeaseId == lease.LeaseId &&
            run.WorkerId == lease.WorkerId &&
            run.FencingToken == lease.FencingToken)
        {
            return true;
        }

        run = null!;
        return false;
    }

    private static void ValidateRequest(AgentRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.HandlerId);
        ArgumentNullException.ThrowIfNull(request.InputJson);
        if (request.IdempotencyKey is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);
        if (request.ScopeKey is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(request.ScopeKey);
        if (request.BatchId is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(request.BatchId);
        if (request.CaseId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.CaseId);
            if (request.BatchId is null)
                throw new ArgumentException("A case ID requires a batch ID.", nameof(request));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxAttempts, 1);
    }

    private static void ValidateFailure(AgentRunFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentException.ThrowIfNullOrWhiteSpace(failure.Code);
        ArgumentException.ThrowIfNullOrWhiteSpace(failure.Message);
    }

    private static string? GetIdempotencyKey(AgentRunRequest request)
        => request.IdempotencyKey is null ? null : $"{request.ScopeKey ?? string.Empty}\0{request.IdempotencyKey}";

    private static bool SameScope(string? first, string? second)
        => string.Equals(first, second, StringComparison.Ordinal);

    private bool IsBelowMaxLeasedRunsPerScope(MutableRun candidate)
    {
        if (_options.MaxLeasedRunsPerScope is not { } limit)
            return true;

        return _runs.Values.Count(active => active.Status == AgentRunStatus.Leased &&
            SameScope(active.Request.ScopeKey, candidate.Request.ScopeKey)) < limit;
    }

    private static bool SameIdempotentRequest(AgentRunRequest existing, AgentRunRequest requested)
        => string.Equals(existing.HandlerId, requested.HandlerId, StringComparison.Ordinal) &&
            string.Equals(existing.InputJson, requested.InputJson, StringComparison.Ordinal) &&
            string.Equals(existing.ScopeKey, requested.ScopeKey, StringComparison.Ordinal) &&
            string.Equals(existing.BatchId, requested.BatchId, StringComparison.Ordinal) &&
            string.Equals(existing.CaseId, requested.CaseId, StringComparison.Ordinal);

    private static bool IsPastDeadline(MutableRun run, DateTimeOffset now)
        => run.Request.Deadline is { } deadline && deadline <= now;

    private static bool IsTerminal(AgentRunStatus status)
        => status is AgentRunStatus.Succeeded or AgentRunStatus.Failed or
            AgentRunStatus.Cancelled or AgentRunStatus.DeadlineExceeded;

    private static void ExpireLease(MutableRun run, DateTimeOffset now)
    {
        if (run.CancellationRequested)
        {
            SetTerminal(run, AgentRunStatus.Cancelled, null, now);
            return;
        }

        if (IsPastDeadline(run, now))
        {
            SetTerminal(run, AgentRunStatus.DeadlineExceeded, "The run deadline elapsed during execution.", now);
            return;
        }

        run.FailureCount++;
        run.FailureCode = "lease_expired";
        run.FailureMessage = "The worker lease expired before the run was acknowledged.";
        if (run.FailureCount >= run.Request.MaxAttempts)
        {
            SetTerminal(run, AgentRunStatus.Failed, run.FailureMessage, now);
            run.FailureCode = "lease_expired";
        }
        else
        {
            run.Status = AgentRunStatus.RetryScheduled;
            run.NextAttemptAt = now;
            run.UpdatedAt = now;
            ClearLease(run);
        }
    }

    private static void SetTerminal(
        MutableRun run,
        AgentRunStatus status,
        string? failureMessage,
        DateTimeOffset now)
    {
        run.Status = status;
        run.FailureMessage = failureMessage;
        run.NextAttemptAt = null;
        run.UpdatedAt = now;
        ClearLease(run);
    }

    private static void ClearLease(MutableRun run)
    {
        run.WorkerId = null;
        run.LeaseId = null;
        run.LeaseExpiresAt = null;
        run.CancellationRequested = false;
    }

    private sealed class MutableRun
    {
        public string RunId { get; }
        public AgentRunRequest Request { get; }
        public AgentRunStatus Status { get; set; } = AgentRunStatus.Queued;
        public int Attempt { get; set; }
        public int FailureCount { get; set; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? NextAttemptAt { get; set; }
        public string? OutputJson { get; set; }
        public string? FailureCode { get; set; }
        public string? FailureMessage { get; set; }
        public string? CheckpointJson { get; set; }
        public string? ResumeInputJson { get; set; }
        public string? PendingInputDescription { get; set; }
        public string? WorkerId { get; set; }
        public string? LeaseId { get; set; }
        public long FencingToken { get; set; }
        public DateTimeOffset? LeaseExpiresAt { get; set; }
        public bool CancellationRequested { get; set; }

        public MutableRun(string runId, AgentRunRequest request, DateTimeOffset now)
        {
            RunId = runId;
            Request = request;
            CreatedAt = now;
            UpdatedAt = now;
        }

        public AgentRunLease ToLease()
            => new(
                RunId,
                Request,
                WorkerId!,
                LeaseId!,
                FencingToken,
                Attempt,
                FailureCount,
                LeaseExpiresAt!.Value,
                CheckpointJson,
                ResumeInputJson,
                CancellationRequested);

        public AgentRunRecord ToRecord()
            => new(
                RunId,
                Request,
                Status,
                Attempt,
                FailureCount,
                CreatedAt,
                UpdatedAt,
                NextAttemptAt,
                OutputJson,
                FailureCode,
                FailureMessage,
                CheckpointJson,
                ResumeInputJson,
                PendingInputDescription,
                WorkerId,
                LeaseId,
                Status == AgentRunStatus.Leased ? FencingToken : null,
                LeaseExpiresAt,
                CancellationRequested);
    }
}
