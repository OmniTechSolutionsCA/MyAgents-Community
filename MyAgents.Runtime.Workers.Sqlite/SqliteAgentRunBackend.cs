using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MyAgents.Core.Execution;

namespace MyAgents.Runtime.Workers.Sqlite;

/// <summary>
/// SQLite-backed run lifecycle store for durable single-machine deployments and development.
/// </summary>
/// <remarks>
/// Claims are serialized by SQLite write transactions and leases survive process restarts. Use a
/// database on reliable local storage; SQLite is not a distributed database and this adapter does
/// not establish multi-host throughput or availability.
/// </remarks>
public sealed class SqliteAgentRunBackend : IAgentRunBackend, IAgentRunBatchCatalog, IAgentRunBatchController
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly AgentRunBackendOptions _options;

    /// <summary>Opens or creates a SQLite run database at the supplied file path.</summary>
    public SqliteAgentRunBackend(string databasePath, TimeProvider? timeProvider = null)
        : this(databasePath, timeProvider, options: null)
    {
    }

    /// <summary>Opens or creates a SQLite run database with optional per-scope limits.</summary>
    public SqliteAgentRunBackend(
        string databasePath,
        TimeProvider? timeProvider,
        AgentRunBackendOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30,
        }.ToString();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new AgentRunBackendOptions();
        _options.Validate();
        InitializeDatabase();
    }

    /// <inheritdoc />
    public async ValueTask<AgentRunSubmission> SubmitAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        if (request.IdempotencyKey is not null)
        {
            var existing = await FindByIdempotencyKeyAsync(
                connection,
                transaction,
                request.ScopeKey,
                request.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!SameIdempotentRequest(existing.Record.Request, request))
                    throw new InvalidOperationException("The idempotency key is already associated with a different run request.");

                transaction.Commit();
                return new AgentRunSubmission(existing.Record.RunId, WasDeduplicated: true);
            }
        }

        if (_options.MaxOutstandingRunsPerScope is { } outstandingLimit &&
            await CountOutstandingInScopeAsync(connection, transaction, request.ScopeKey, cancellationToken).ConfigureAwait(false) >= outstandingLimit)
        {
            throw new AgentRunAdmissionRejectedException(request.ScopeKey, outstandingLimit);
        }

        var now = _timeProvider.GetUtcNow();
        var record = new AgentRunRecord(
            Guid.NewGuid().ToString("N"),
            request,
            AgentRunStatus.Queued,
            Attempt: 0,
            FailureCount: 0,
            CreatedAt: now,
            UpdatedAt: now);
        await InsertAsync(connection, transaction, record, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return new AgentRunSubmission(record.RunId, WasDeduplicated: false);
    }

    /// <inheritdoc />
    public async ValueTask<AgentRunRecord?> GetAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var stored = await FindByIdAsync(connection, transaction: null, runId, cancellationToken).ConfigureAwait(false);
        return stored?.Record;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AgentRunRecord>> GetBatchRunsAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT record_json FROM agent_runs WHERE batch_id = $batch_id ORDER BY created_ticks, run_id";
        command.Parameters.AddWithValue("$batch_id", batchId);
        var records = new List<AgentRunRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var record = JsonSerializer.Deserialize<AgentRunRecord>(reader.GetString(0), s_jsonOptions)
                ?? throw new InvalidOperationException("A persisted run record could not be deserialized.");
            records.Add(record);
        }

        return records;
    }

    /// <inheritdoc />
    public async ValueTask<int> CancelBatchAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var affectedRuns = await FindManyAsync(
            connection,
            transaction,
            "batch_id = $batch_id AND status NOT IN ($succeeded, $failed, $cancelled, $deadline)",
            command =>
            {
                command.Parameters.AddWithValue("$batch_id", batchId);
                command.Parameters.AddWithValue("$succeeded", (int)AgentRunStatus.Succeeded);
                command.Parameters.AddWithValue("$failed", (int)AgentRunStatus.Failed);
                command.Parameters.AddWithValue("$cancelled", (int)AgentRunStatus.Cancelled);
                command.Parameters.AddWithValue("$deadline", (int)AgentRunStatus.DeadlineExceeded);
            },
            cancellationToken).ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        foreach (var stored in affectedRuns)
        {
            var updated = stored.Record.Status == AgentRunStatus.Leased
                ? stored.Record with { CancellationRequested = true, UpdatedAt = now }
                : SetTerminal(stored.Record, AgentRunStatus.Cancelled, null, now);
            await PersistAsync(connection, transaction, updated, stored.FencingToken, cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
        return affectedRuns.Count;
    }

    /// <inheritdoc />
    public async ValueTask<AgentRunLease?> ClaimAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = _timeProvider.GetUtcNow();
        var nowTicks = ToTicks(now);

        var expiredLeases = await FindManyAsync(
            connection,
            transaction,
            "status = $status AND lease_expires_ticks <= $now",
            command =>
            {
                command.Parameters.AddWithValue("$status", (int)AgentRunStatus.Leased);
                command.Parameters.AddWithValue("$now", nowTicks);
            },
            cancellationToken).ConfigureAwait(false);
        foreach (var stored in expiredLeases)
            await PersistAsync(connection, transaction, ExpireLease(stored.Record, now), stored.FencingToken, cancellationToken).ConfigureAwait(false);

        var overdueRuns = await FindManyAsync(
            connection,
            transaction,
            "status IN ($queued, $waiting, $retry) AND deadline_ticks IS NOT NULL AND deadline_ticks <= $now",
            command =>
            {
                command.Parameters.AddWithValue("$queued", (int)AgentRunStatus.Queued);
                command.Parameters.AddWithValue("$waiting", (int)AgentRunStatus.WaitingForInput);
                command.Parameters.AddWithValue("$retry", (int)AgentRunStatus.RetryScheduled);
                command.Parameters.AddWithValue("$now", nowTicks);
            },
            cancellationToken).ConfigureAwait(false);
        foreach (var stored in overdueRuns)
        {
            var expired = SetTerminal(
                stored.Record,
                AgentRunStatus.DeadlineExceeded,
                "The run deadline elapsed before execution.",
                now);
            await PersistAsync(connection, transaction, expired, stored.FencingToken, cancellationToken).ConfigureAwait(false);
        }

        var dueRetries = await FindManyAsync(
            connection,
            transaction,
            "status = $status AND next_attempt_ticks <= $now",
            command =>
            {
                command.Parameters.AddWithValue("$status", (int)AgentRunStatus.RetryScheduled);
                command.Parameters.AddWithValue("$now", nowTicks);
            },
            cancellationToken).ConfigureAwait(false);
        foreach (var stored in dueRetries)
        {
            var queued = stored.Record with
            {
                Status = AgentRunStatus.Queued,
                NextAttemptAt = null,
                UpdatedAt = now,
            };
            await PersistAsync(connection, transaction, queued, stored.FencingToken, cancellationToken).ConfigureAwait(false);
        }

        var candidate = await FindFirstQueuedAsync(
            connection,
            transaction,
            _options.MaxLeasedRunsPerScope,
            cancellationToken).ConfigureAwait(false);
        if (candidate is null)
        {
            transaction.Commit();
            return null;
        }

        var fencingToken = checked(candidate.FencingToken + 1);
        var expiresAt = now + leaseDuration;
        var claimed = candidate.Record with
        {
            Status = AgentRunStatus.Leased,
            Attempt = candidate.Record.Attempt + 1,
            UpdatedAt = now,
            WorkerId = workerId,
            LeaseId = Guid.NewGuid().ToString("N"),
            FencingToken = fencingToken,
            LeaseExpiresAt = expiresAt,
            CancellationRequested = false,
        };
        await PersistAsync(connection, transaction, claimed, fencingToken, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return ToLease(claimed, fencingToken);
    }

    /// <inheritdoc />
    public async ValueTask<AgentRunLeaseRenewal> RenewLeaseAsync(
        AgentRunLease lease,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = _timeProvider.GetUtcNow();
        var stored = await FindByIdAsync(connection, transaction, lease.RunId, cancellationToken).ConfigureAwait(false);
        if (stored is null || !IsCurrentLease(stored, lease, now))
        {
            transaction.Commit();
            return new AgentRunLeaseRenewal(false, false);
        }

        if (IsPastDeadline(stored.Record, now))
        {
            var expired = SetTerminal(stored.Record, AgentRunStatus.DeadlineExceeded, "The run deadline elapsed during execution.", now);
            await PersistAsync(connection, transaction, expired, stored.FencingToken, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return new AgentRunLeaseRenewal(false, false);
        }

        var expiresAt = now + leaseDuration;
        var renewed = stored.Record with { LeaseExpiresAt = expiresAt, UpdatedAt = now };
        await PersistAsync(connection, transaction, renewed, stored.FencingToken, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return new AgentRunLeaseRenewal(true, renewed.CancellationRequested, expiresAt);
    }

    /// <inheritdoc />
    public async ValueTask<bool> SaveCheckpointAsync(
        AgentRunLease lease,
        string checkpointJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(checkpointJson);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = _timeProvider.GetUtcNow();
        var stored = await FindByIdAsync(connection, transaction, lease.RunId, cancellationToken).ConfigureAwait(false);
        if (stored is null || !IsCurrentLease(stored, lease, now) ||
            stored.Record.CancellationRequested || IsPastDeadline(stored.Record, now))
        {
            transaction.Commit();
            return false;
        }

        var updated = stored.Record with { CheckpointJson = checkpointJson, UpdatedAt = now };
        await PersistAsync(connection, transaction, updated, stored.FencingToken, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<bool> CompleteAsync(
        AgentRunLease lease,
        string? outputJson = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = _timeProvider.GetUtcNow();
        var stored = await FindByIdAsync(connection, transaction, lease.RunId, cancellationToken).ConfigureAwait(false);
        if (stored is null || !IsCurrentLease(stored, lease, now))
        {
            transaction.Commit();
            return false;
        }

        var result = stored.Record.CancellationRequested
            ? SetTerminal(stored.Record, AgentRunStatus.Cancelled, null, now)
            : IsPastDeadline(stored.Record, now)
                ? SetTerminal(stored.Record, AgentRunStatus.DeadlineExceeded, "The run deadline elapsed during execution.", now)
                : SetTerminal(stored.Record with { OutputJson = outputJson }, AgentRunStatus.Succeeded, null, now);
        await PersistAsync(connection, transaction, result, stored.FencingToken, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<bool> WaitForInputAsync(
        AgentRunLease lease,
        string checkpointJson,
        string pendingInputDescription,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(checkpointJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingInputDescription);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = _timeProvider.GetUtcNow();
        var stored = await FindByIdAsync(connection, transaction, lease.RunId, cancellationToken).ConfigureAwait(false);
        if (stored is null || !IsCurrentLease(stored, lease, now))
        {
            transaction.Commit();
            return false;
        }

        var result = stored.Record.CancellationRequested
            ? SetTerminal(stored.Record, AgentRunStatus.Cancelled, null, now)
            : IsPastDeadline(stored.Record, now)
                ? SetTerminal(stored.Record, AgentRunStatus.DeadlineExceeded, "The run deadline elapsed during execution.", now)
                : stored.Record with
                {
                    Status = AgentRunStatus.WaitingForInput,
                    CheckpointJson = checkpointJson,
                    ResumeInputJson = null,
                    PendingInputDescription = pendingInputDescription,
                    UpdatedAt = now,
                    WorkerId = null,
                    LeaseId = null,
                    LeaseExpiresAt = null,
                    FencingToken = null,
                    CancellationRequested = false,
                };
        await PersistAsync(connection, transaction, result, stored.FencingToken, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<bool> ResumeAsync(
        string runId,
        string resumeInputJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(resumeInputJson);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var stored = await FindByIdAsync(connection, transaction, runId, cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.Record.Status != AgentRunStatus.WaitingForInput)
        {
            transaction.Commit();
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        if (IsPastDeadline(stored.Record, now))
        {
            var expired = SetTerminal(stored.Record, AgentRunStatus.DeadlineExceeded, "The run deadline elapsed while waiting for input.", now);
            await PersistAsync(connection, transaction, expired, stored.FencingToken, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return false;
        }

        var resumed = stored.Record with
        {
            ResumeInputJson = resumeInputJson,
            PendingInputDescription = null,
            Status = AgentRunStatus.Queued,
            UpdatedAt = now,
        };
        await PersistAsync(connection, transaction, resumed, stored.FencingToken, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<bool> RetryAsync(
        AgentRunLease lease,
        AgentRunFailure failure,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateFailure(failure);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryDelay, TimeSpan.Zero);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = _timeProvider.GetUtcNow();
        var stored = await FindByIdAsync(connection, transaction, lease.RunId, cancellationToken).ConfigureAwait(false);
        if (stored is null || !IsCurrentLease(stored, lease, now))
        {
            transaction.Commit();
            return false;
        }

        if (stored.Record.CancellationRequested)
        {
            var cancelled = SetTerminal(stored.Record, AgentRunStatus.Cancelled, null, now);
            await PersistAsync(connection, transaction, cancelled, stored.FencingToken, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return true;
        }
        else if (IsPastDeadline(stored.Record, now))
        {
            var expired = SetTerminal(stored.Record, AgentRunStatus.DeadlineExceeded, "The run deadline elapsed during execution.", now);
            await PersistAsync(connection, transaction, expired, stored.FencingToken, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return true;
        }

        var updated = stored.Record with
        {
            FailureCount = stored.Record.FailureCount + 1,
            FailureCode = failure.Code,
            FailureMessage = failure.Message,
        };
        if (!failure.Retryable || updated.FailureCount >= updated.Request.MaxAttempts)
            updated = SetTerminal(updated, AgentRunStatus.Failed, failure.Message, now);
        else
            updated = ClearLease(updated with { Status = AgentRunStatus.RetryScheduled, NextAttemptAt = now + retryDelay, UpdatedAt = now });

        await PersistAsync(connection, transaction, updated, stored.FencingToken, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<bool> FailAsync(
        AgentRunLease lease,
        AgentRunFailure failure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateFailure(failure);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = _timeProvider.GetUtcNow();
        var stored = await FindByIdAsync(connection, transaction, lease.RunId, cancellationToken).ConfigureAwait(false);
        if (stored is null || !IsCurrentLease(stored, lease, now))
        {
            transaction.Commit();
            return false;
        }

        var updated = stored.Record.CancellationRequested
            ? SetTerminal(stored.Record, AgentRunStatus.Cancelled, null, now)
            : IsPastDeadline(stored.Record, now)
                ? SetTerminal(stored.Record, AgentRunStatus.DeadlineExceeded, failure.Message, now)
                : SetTerminal(stored.Record with { FailureCode = failure.Code }, AgentRunStatus.Failed, failure.Message, now);
        await PersistAsync(connection, transaction, updated, stored.FencingToken, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<bool> CancelAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var stored = await FindByIdAsync(connection, transaction, runId, cancellationToken).ConfigureAwait(false);
        if (stored is null || IsTerminal(stored.Record.Status))
        {
            transaction.Commit();
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        var updated = stored.Record.Status == AgentRunStatus.Leased
            ? stored.Record with { CancellationRequested = true, UpdatedAt = now }
            : SetTerminal(stored.Record, AgentRunStatus.Cancelled, null, now);
        await PersistAsync(connection, transaction, updated, stored.FencingToken, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return true;
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS agent_runs (
                run_id TEXT PRIMARY KEY,
                scope_key TEXT NULL,
                idempotency_key TEXT NULL,
                batch_id TEXT NULL,
                case_id TEXT NULL,
                status INTEGER NOT NULL,
                created_ticks INTEGER NOT NULL,
                deadline_ticks INTEGER NULL,
                next_attempt_ticks INTEGER NULL,
                lease_expires_ticks INTEGER NULL,
                fencing_token INTEGER NOT NULL,
                record_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS agent_run_configuration (
                config_id INTEGER PRIMARY KEY CHECK(config_id = 1),
                max_outstanding_runs_per_scope INTEGER NULL,
                max_leased_runs_per_scope INTEGER NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_runs_idempotency
                ON agent_runs(ifnull(scope_key, ''), idempotency_key)
                WHERE idempotency_key IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_agent_runs_queue
                ON agent_runs(status, created_ticks);
            CREATE INDEX IF NOT EXISTS ix_agent_runs_lease_expiration
                ON agent_runs(status, lease_expires_ticks);
            CREATE INDEX IF NOT EXISTS ix_agent_runs_deadlines
                ON agent_runs(status, deadline_ticks);
            CREATE INDEX IF NOT EXISTS ix_agent_runs_retry_schedule
                ON agent_runs(status, next_attempt_ticks);
            """;
        command.ExecuteNonQuery();

        EnsureColumnExists(connection, "batch_id");
        EnsureColumnExists(connection, "case_id");
        command.CommandText = "CREATE INDEX IF NOT EXISTS ix_agent_runs_batch ON agent_runs(batch_id, created_ticks, run_id)";
        command.ExecuteNonQuery();
        ValidateSharedOptions(connection, command);
    }

    private async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous = NORMAL";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentRunRecord record,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agent_runs (
                run_id, scope_key, idempotency_key, batch_id, case_id, status, created_ticks, deadline_ticks,
                next_attempt_ticks, lease_expires_ticks, fencing_token, record_json)
            VALUES ($run_id, $scope_key, $idempotency_key, $batch_id, $case_id, $status, $created_ticks, $deadline_ticks,
                NULL, NULL, 0, $record_json)
            """;
        AddRecordParameters(command, record, fencingToken: 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask PersistAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentRunRecord record,
        long fencingToken,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE agent_runs SET
                scope_key = $scope_key,
                idempotency_key = $idempotency_key,
                batch_id = $batch_id,
                case_id = $case_id,
                status = $status,
                created_ticks = $created_ticks,
                deadline_ticks = $deadline_ticks,
                next_attempt_ticks = $next_attempt_ticks,
                lease_expires_ticks = $lease_expires_ticks,
                fencing_token = $fencing_token,
                record_json = $record_json
            WHERE run_id = $run_id
            """;
        AddRecordParameters(command, record, fencingToken);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException($"Run '{record.RunId}' disappeared during a transaction.");
    }

    private static void AddRecordParameters(SqliteCommand command, AgentRunRecord record, long fencingToken)
    {
        command.Parameters.AddWithValue("$run_id", record.RunId);
        command.Parameters.AddWithValue("$scope_key", (object?)record.Request.ScopeKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$idempotency_key", (object?)record.Request.IdempotencyKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$batch_id", (object?)record.Request.BatchId ?? DBNull.Value);
        command.Parameters.AddWithValue("$case_id", (object?)record.Request.CaseId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (int)record.Status);
        command.Parameters.AddWithValue("$created_ticks", ToTicks(record.CreatedAt));
        command.Parameters.AddWithValue("$deadline_ticks", (object?)ToTicks(record.Request.Deadline) ?? DBNull.Value);
        command.Parameters.AddWithValue("$next_attempt_ticks", (object?)ToTicks(record.NextAttemptAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$lease_expires_ticks", (object?)ToTicks(record.LeaseExpiresAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$fencing_token", fencingToken);
        command.Parameters.AddWithValue("$record_json", JsonSerializer.Serialize(record, s_jsonOptions));
    }

    private static async ValueTask<StoredRun?> FindByIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT record_json, fencing_token FROM agent_runs WHERE run_id = $run_id";
        command.Parameters.AddWithValue("$run_id", runId);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<StoredRun?> FindByIdempotencyKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? scopeKey,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT record_json, fencing_token FROM agent_runs
            WHERE ifnull(scope_key, '') = ifnull($scope_key, '') AND idempotency_key = $idempotency_key
            """;
        command.Parameters.AddWithValue("$scope_key", (object?)scopeKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$idempotency_key", idempotencyKey);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<List<StoredRun>> FindManyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string predicate,
        Action<SqliteCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT record_json, fencing_token FROM agent_runs WHERE {predicate}";
        configure(command);
        var results = new List<StoredRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(ParseStoredRun(reader));
        return results;
    }

    private static async ValueTask<StoredRun?> FindFirstQueuedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int? maxLeasedRunsPerScope,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT queued.record_json, queued.fencing_token
            FROM agent_runs AS queued
            WHERE queued.status = $queued_status
              AND ($scope_limit IS NULL OR (
                  SELECT COUNT(*) FROM agent_runs AS active
                  WHERE active.status = $leased_status
                    AND ifnull(active.scope_key, '') = ifnull(queued.scope_key, '')
              ) < $scope_limit)
            ORDER BY queued.created_ticks, queued.run_id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$queued_status", (int)AgentRunStatus.Queued);
        command.Parameters.AddWithValue("$leased_status", (int)AgentRunStatus.Leased);
        command.Parameters.AddWithValue("$scope_limit", (object?)maxLeasedRunsPerScope ?? DBNull.Value);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> CountOutstandingInScopeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? scopeKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM agent_runs
            WHERE ifnull(scope_key, '') = ifnull($scope_key, '')
              AND status NOT IN ($succeeded, $failed, $cancelled, $deadline)
            """;
        command.Parameters.AddWithValue("$scope_key", (object?)scopeKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$succeeded", (int)AgentRunStatus.Succeeded);
        command.Parameters.AddWithValue("$failed", (int)AgentRunStatus.Failed);
        command.Parameters.AddWithValue("$cancelled", (int)AgentRunStatus.Cancelled);
        command.Parameters.AddWithValue("$deadline", (int)AgentRunStatus.DeadlineExceeded);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static void EnsureColumnExists(SqliteConnection connection, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(agent_runs)";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return;
        }

        reader.Close();
        command.CommandText = columnName switch
        {
            "batch_id" => "ALTER TABLE agent_runs ADD COLUMN batch_id TEXT NULL",
            "case_id" => "ALTER TABLE agent_runs ADD COLUMN case_id TEXT NULL",
            _ => throw new ArgumentOutOfRangeException(nameof(columnName)),
        };
        command.ExecuteNonQuery();
    }

    private void ValidateSharedOptions(SqliteConnection connection, SqliteCommand command)
    {
        command.Parameters.Clear();
        command.CommandText = """
            INSERT OR IGNORE INTO agent_run_configuration (
                config_id, max_outstanding_runs_per_scope, max_leased_runs_per_scope)
            VALUES (1, $max_outstanding, $max_leased)
            """;
        command.Parameters.AddWithValue("$max_outstanding", (object?)_options.MaxOutstandingRunsPerScope ?? DBNull.Value);
        command.Parameters.AddWithValue("$max_leased", (object?)_options.MaxLeasedRunsPerScope ?? DBNull.Value);
        command.ExecuteNonQuery();

        command.Parameters.Clear();
        command.CommandText = """
            SELECT max_outstanding_runs_per_scope, max_leased_runs_per_scope
            FROM agent_run_configuration WHERE config_id = 1
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("The SQLite run backend configuration row is missing.");

        int? maxOutstanding = reader.IsDBNull(0) ? null : reader.GetInt32(0);
        int? maxLeased = reader.IsDBNull(1) ? null : reader.GetInt32(1);
        if (maxOutstanding != _options.MaxOutstandingRunsPerScope || maxLeased != _options.MaxLeasedRunsPerScope)
        {
            throw new InvalidOperationException(
                "All SqliteAgentRunBackend instances sharing a database must use the same per-scope run limits.");
        }
    }

    private static async ValueTask<StoredRun?> ReadSingleAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ParseStoredRun(reader) : null;
    }

    private static StoredRun ParseStoredRun(SqliteDataReader reader)
    {
        var record = JsonSerializer.Deserialize<AgentRunRecord>(reader.GetString(0), s_jsonOptions)
            ?? throw new InvalidOperationException("A persisted run record could not be deserialized.");
        return new StoredRun(record, reader.GetInt64(1));
    }

    private static AgentRunLease ToLease(AgentRunRecord record, long fencingToken)
        => new(
            record.RunId,
            record.Request,
            record.WorkerId!,
            record.LeaseId!,
            fencingToken,
            record.Attempt,
            record.FailureCount,
            record.LeaseExpiresAt!.Value,
            record.CheckpointJson,
            record.ResumeInputJson,
            record.CancellationRequested);

    private static bool IsCurrentLease(StoredRun stored, AgentRunLease lease, DateTimeOffset now)
        => stored.Record.Status == AgentRunStatus.Leased &&
            stored.Record.LeaseExpiresAt > now &&
            stored.Record.LeaseId == lease.LeaseId &&
            stored.Record.WorkerId == lease.WorkerId &&
            stored.FencingToken == lease.FencingToken;

    private static AgentRunRecord ExpireLease(AgentRunRecord record, DateTimeOffset now)
    {
        if (record.CancellationRequested)
            return SetTerminal(record, AgentRunStatus.Cancelled, null, now);
        if (IsPastDeadline(record, now))
            return SetTerminal(record, AgentRunStatus.DeadlineExceeded, "The run deadline elapsed during execution.", now);

        var failed = record with
        {
            FailureCount = record.FailureCount + 1,
            FailureCode = "lease_expired",
            FailureMessage = "The worker lease expired before the run was acknowledged.",
        };
        if (failed.FailureCount >= failed.Request.MaxAttempts)
            return SetTerminal(failed, AgentRunStatus.Failed, failed.FailureMessage, now) with { FailureCode = "lease_expired" };
        return ClearLease(failed with { Status = AgentRunStatus.RetryScheduled, NextAttemptAt = now, UpdatedAt = now });
    }

    private static AgentRunRecord SetTerminal(
        AgentRunRecord record,
        AgentRunStatus status,
        string? failureMessage,
        DateTimeOffset now)
        => ClearLease(record with
        {
            Status = status,
            FailureMessage = failureMessage,
            NextAttemptAt = null,
            UpdatedAt = now,
        });

    private static AgentRunRecord ClearLease(AgentRunRecord record)
        => record with
        {
            WorkerId = null,
            LeaseId = null,
            LeaseExpiresAt = null,
            FencingToken = null,
            CancellationRequested = false,
        };

    private static bool IsPastDeadline(AgentRunRecord record, DateTimeOffset now)
        => record.Request.Deadline is { } deadline && deadline <= now;

    private static bool IsTerminal(AgentRunStatus status)
        => status is AgentRunStatus.Succeeded or AgentRunStatus.Failed or AgentRunStatus.Cancelled or AgentRunStatus.DeadlineExceeded;

    private static long ToTicks(DateTimeOffset value) => value.UtcDateTime.Ticks;

    private static long? ToTicks(DateTimeOffset? value) => value is { } dateTime ? ToTicks(dateTime) : null;

    private static bool SameIdempotentRequest(AgentRunRequest existing, AgentRunRequest requested)
        => string.Equals(existing.HandlerId, requested.HandlerId, StringComparison.Ordinal) &&
            string.Equals(existing.InputJson, requested.InputJson, StringComparison.Ordinal) &&
            string.Equals(existing.ScopeKey, requested.ScopeKey, StringComparison.Ordinal) &&
            string.Equals(existing.BatchId, requested.BatchId, StringComparison.Ordinal) &&
            string.Equals(existing.CaseId, requested.CaseId, StringComparison.Ordinal);

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

    private sealed record StoredRun(AgentRunRecord Record, long FencingToken);
}
