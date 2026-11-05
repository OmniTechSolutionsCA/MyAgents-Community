using Microsoft.Data.Sqlite;
using MyAgents.Core.Execution;
using MyAgents.Runtime.Workers;
using MyAgents.Runtime.Workers.Sqlite;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class SqliteAgentRunBackendTests
{
    [Test]
    public async Task Submitted_runs_and_idempotency_survive_backend_recreation()
    {
        using var database = new TemporaryDatabase();
        var request = new AgentRunRequest("handler", "{\"value\":7}", "key-1", "tenant-a");
        string runId;
        var firstBackend = new SqliteAgentRunBackend(database.Path);
        var submitted = await firstBackend.SubmitAsync(request);
        runId = submitted.RunId;
        Assert.That(submitted.WasDeduplicated, Is.False);

        var backend = new SqliteAgentRunBackend(database.Path);
        var duplicate = await backend.SubmitAsync(request with { MaxAttempts = 6 });
        var restored = await backend.GetAsync(runId);

        Assert.That(duplicate.RunId, Is.EqualTo(runId));
        Assert.That(duplicate.WasDeduplicated, Is.True);
        Assert.That(restored!.Request.InputJson, Is.EqualTo(request.InputJson));
        Assert.That(restored.Status, Is.EqualTo(AgentRunStatus.Queued));
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await backend.SubmitAsync(request with { InputJson = "{\"value\":8}" }));
    }

    [Test]
    public async Task Expired_lease_is_recovered_after_restart_and_stale_worker_is_fenced()
    {
        using var database = new TemporaryDatabase();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-11-03T12:00:00Z"));
        var firstBackend = new SqliteAgentRunBackend(database.Path, clock);
        var submission = await firstBackend.SubmitAsync(new AgentRunRequest("handler", "{}"));
        var staleLease = await firstBackend.ClaimAsync("worker-a", TimeSpan.FromSeconds(10));
        Assert.That(staleLease, Is.Not.Null);

        clock.Advance(TimeSpan.FromSeconds(11));
        var restartedBackend = new SqliteAgentRunBackend(database.Path, clock);
        var recoveredLease = await restartedBackend.ClaimAsync("worker-b", TimeSpan.FromSeconds(10));

        Assert.That(recoveredLease, Is.Not.Null);
        Assert.That(recoveredLease!.Attempt, Is.EqualTo(2));
        Assert.That(recoveredLease.FencingToken, Is.GreaterThan(staleLease!.FencingToken));
        Assert.That(await restartedBackend.CompleteAsync(staleLease), Is.False);
        Assert.That(await restartedBackend.CompleteAsync(recoveredLease, "\"done\""), Is.True);
        Assert.That((await restartedBackend.GetAsync(submission.RunId))!.Status, Is.EqualTo(AgentRunStatus.Succeeded));
    }

    [Test]
    public async Task Only_one_of_two_backend_instances_claims_a_run()
    {
        using var database = new TemporaryDatabase();
        var backendA = new SqliteAgentRunBackend(database.Path);
        var backendB = new SqliteAgentRunBackend(database.Path);
        await backendA.SubmitAsync(new AgentRunRequest("handler", "{}"));

        var claims = await Task.WhenAll(
            backendA.ClaimAsync("worker-a", TimeSpan.FromSeconds(30)).AsTask(),
            backendB.ClaimAsync("worker-b", TimeSpan.FromSeconds(30)).AsTask());

        Assert.That(claims.Count(static lease => lease is not null), Is.EqualTo(1));
    }

    [Test]
    public async Task Wait_and_resume_state_survives_backend_recreation()
    {
        using var database = new TemporaryDatabase();
        var firstBackend = new SqliteAgentRunBackend(database.Path);
        var submission = await firstBackend.SubmitAsync(new AgentRunRequest("handler", "{}"));
        var lease = await firstBackend.ClaimAsync("worker-a", TimeSpan.FromSeconds(30));
        Assert.That(await firstBackend.WaitForInputAsync(lease!, "snapshot-json", "Approval needed"), Is.True);

        var restartedBackend = new SqliteAgentRunBackend(database.Path);
        Assert.That(await restartedBackend.ResumeAsync(submission.RunId, "{\"approved\":true}"), Is.True);
        var resumedLease = await restartedBackend.ClaimAsync("worker-b", TimeSpan.FromSeconds(30));

        Assert.That(resumedLease!.CheckpointJson, Is.EqualTo("snapshot-json"));
        Assert.That(resumedLease.ResumeInputJson, Is.EqualTo("{\"approved\":true}"));
    }

    [Test]
    public async Task Retry_delay_and_attempt_limit_are_persisted()
    {
        using var database = new TemporaryDatabase();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-11-03T12:00:00Z"));
        var backend = new SqliteAgentRunBackend(database.Path, clock);
        var submission = await backend.SubmitAsync(new AgentRunRequest("handler", "{}", MaxAttempts: 2));
        var firstLease = await backend.ClaimAsync("worker-a", TimeSpan.FromSeconds(30));
        Assert.That(await backend.RetryAsync(
            firstLease!,
            new AgentRunFailure("transient", "Try again.", Retryable: true),
            TimeSpan.FromSeconds(10)), Is.True);

        var restartedBackend = new SqliteAgentRunBackend(database.Path, clock);
        Assert.That(await restartedBackend.ClaimAsync("worker-b", TimeSpan.FromSeconds(30)), Is.Null);
        clock.Advance(TimeSpan.FromSeconds(10));
        var secondLease = await restartedBackend.ClaimAsync("worker-b", TimeSpan.FromSeconds(30));
        Assert.That(secondLease!.Attempt, Is.EqualTo(2));
        Assert.That(await restartedBackend.RetryAsync(
            secondLease,
            new AgentRunFailure("transient", "Attempts exhausted.", Retryable: true),
            TimeSpan.Zero), Is.True);

        var record = await restartedBackend.GetAsync(submission.RunId);
        Assert.That(record!.Status, Is.EqualTo(AgentRunStatus.Failed));
        Assert.That(record.FailureCount, Is.EqualTo(2));
    }

    [Test]
    public async Task Deadline_expires_a_waiting_run_during_queue_polling()
    {
        using var database = new TemporaryDatabase();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-11-03T12:00:00Z"));
        var backend = new SqliteAgentRunBackend(database.Path, clock);
        var submission = await backend.SubmitAsync(new AgentRunRequest(
            "handler",
            "{}",
            Deadline: clock.GetUtcNow().AddMinutes(1)));
        var lease = await backend.ClaimAsync("worker-a", TimeSpan.FromSeconds(30));
        Assert.That(await backend.WaitForInputAsync(lease!, "snapshot", "Reply required"), Is.True);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.That(await backend.ClaimAsync("worker-b", TimeSpan.FromSeconds(30)), Is.Null);
        Assert.That((await backend.GetAsync(submission.RunId))!.Status, Is.EqualTo(AgentRunStatus.DeadlineExceeded));
    }

    [Test]
    public async Task Per_scope_admission_and_claim_limits_are_enforced_transactionally()
    {
        using var database = new TemporaryDatabase();
        var options = new AgentRunBackendOptions
        {
            MaxOutstandingRunsPerScope = 2,
            MaxLeasedRunsPerScope = 1,
        };
        var backend = new SqliteAgentRunBackend(database.Path, timeProvider: null, options: options);
        var first = await backend.SubmitAsync(new AgentRunRequest("handler", "{}", ScopeKey: "scope-a"));
        var second = await backend.SubmitAsync(new AgentRunRequest("handler", "{}", ScopeKey: "scope-a"));
        await backend.SubmitAsync(new AgentRunRequest("handler", "{}", ScopeKey: "scope-b"));
        Assert.ThrowsAsync<AgentRunAdmissionRejectedException>(async () =>
            await backend.SubmitAsync(new AgentRunRequest("handler", "{}", ScopeKey: "scope-a")));

        var leaseA = await backend.ClaimAsync("worker-a", TimeSpan.FromSeconds(30));
        var leaseB = await backend.ClaimAsync("worker-b", TimeSpan.FromSeconds(30));
        Assert.That(leaseA!.Request.ScopeKey, Is.EqualTo("scope-a"));
        Assert.That(leaseB!.Request.ScopeKey, Is.EqualTo("scope-b"));
        Assert.That(await backend.CompleteAsync(leaseA), Is.True);
        var nextLeaseA = await backend.ClaimAsync("worker-c", TimeSpan.FromSeconds(30));
        Assert.That(nextLeaseA!.RunId, Is.EqualTo(second.RunId));
        Assert.That((await backend.GetAsync(first.RunId))!.Status, Is.EqualTo(AgentRunStatus.Succeeded));
    }

    [Test]
    public void Backend_instances_must_agree_on_persisted_scope_limits()
    {
        using var database = new TemporaryDatabase();
        var options = new AgentRunBackendOptions { MaxLeasedRunsPerScope = 2 };
        _ = new SqliteAgentRunBackend(database.Path, timeProvider: null, options);

        Assert.Throws<InvalidOperationException>(() => new SqliteAgentRunBackend(
            database.Path,
            timeProvider: null,
            options: new AgentRunBackendOptions { MaxLeasedRunsPerScope = 3 }));
    }

    [Test]
    public async Task Batch_case_metadata_and_outcome_queries_survive_restart()
    {
        using var database = new TemporaryDatabase();
        var backend = new SqliteAgentRunBackend(database.Path);
        var submitter = new AgentRunBatchSubmitter(backend);
        await foreach (var _ in submitter.SubmitAsync("batch-sqlite", CreateCases()))
        {
        }

        var restartedBackend = new SqliteAgentRunBackend(database.Path);
        var records = await restartedBackend.GetBatchRunsAsync("batch-sqlite");
        Assert.That(records.Select(static record => record.Request.CaseId), Is.EqualTo(new[] { "case-a", "case-b" }));
        Assert.That(records.All(static record => record.Request.BatchId == "batch-sqlite"), Is.True);
    }

    [Test]
    public async Task Batch_cancellation_is_persisted_for_queued_and_leased_runs()
    {
        using var database = new TemporaryDatabase();
        var backend = new SqliteAgentRunBackend(database.Path);
        await backend.SubmitAsync(new AgentRunRequest("handler", "{}", BatchId: "batch-cancel", CaseId: "leased"));
        var queued = await backend.SubmitAsync(new AgentRunRequest("handler", "{}", BatchId: "batch-cancel", CaseId: "queued"));
        var lease = await backend.ClaimAsync("worker-a", TimeSpan.FromSeconds(30));

        var affected = await new AgentRunBatchController(backend).CancelAsync("batch-cancel");
        var renewal = await backend.RenewLeaseAsync(lease!, TimeSpan.FromSeconds(30));
        Assert.That(affected, Is.EqualTo(2));
        Assert.That(renewal.CancellationRequested, Is.True);
        Assert.That((await backend.GetAsync(queued.RunId))!.Status, Is.EqualTo(AgentRunStatus.Cancelled));
        Assert.That(await backend.CompleteAsync(lease!), Is.True);
        Assert.That((await backend.GetAsync(lease!.RunId))!.Status, Is.EqualTo(AgentRunStatus.Cancelled));
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private DateTimeOffset _now = initialTime;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private static async IAsyncEnumerable<AgentRunBatchCase> CreateCases()
    {
        yield return new AgentRunBatchCase("case-a", new AgentRunRequest("handler", "{\"value\":1}"));
        await Task.Yield();
        yield return new AgentRunBatchCase("case-b", new AgentRunRequest("handler", "{\"value\":2}"));
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"myagents-runs-{Guid.NewGuid():N}.db");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { Path, $"{Path}-wal", $"{Path}-shm" })
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
        }
    }
}
