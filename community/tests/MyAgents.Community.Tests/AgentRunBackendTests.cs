using MyAgents.Core.Execution;
using MyAgents.Core;
using MyAgents.Runtime.Workers;
using MyAgents.Testing;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class AgentRunBackendTests
{
    [Test]
    public async Task Submit_deduplicates_matching_scoped_requests_and_rejects_key_reuse_for_other_input()
    {
        var backend = new InMemoryAgentRunBackend();
        var request = new AgentRunRequest(
            "summarize",
            "{\"item\":1}",
            IdempotencyKey: "request-42",
            ScopeKey: "tenant-a",
            MaxAttempts: 2);

        var first = await backend.SubmitAsync(request);
        var duplicate = await backend.SubmitAsync(request with { MaxAttempts = 5 });
        var otherScope = await backend.SubmitAsync(request with { ScopeKey = "tenant-b" });

        Assert.That(duplicate.RunId, Is.EqualTo(first.RunId));
        Assert.That(duplicate.WasDeduplicated, Is.True);
        Assert.That(otherScope.RunId, Is.Not.EqualTo(first.RunId));
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await backend.SubmitAsync(request with { InputJson = "{\"item\":2}" }));
    }

    [Test]
    public async Task Expired_lease_is_reclaimed_with_new_fencing_token_and_rejects_stale_worker()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-11-03T12:00:00Z"));
        var backend = new InMemoryAgentRunBackend(clock);
        var submission = await backend.SubmitAsync(new AgentRunRequest("handler", "{}"));
        var staleLease = await backend.ClaimAsync("worker-a", TimeSpan.FromSeconds(10));
        Assert.That(staleLease, Is.Not.Null);

        clock.Advance(TimeSpan.FromSeconds(11));
        var currentLease = await backend.ClaimAsync("worker-b", TimeSpan.FromSeconds(10));

        Assert.That(currentLease, Is.Not.Null);
        Assert.That(currentLease!.FencingToken, Is.GreaterThan(staleLease!.FencingToken));
        Assert.That((await backend.RenewLeaseAsync(staleLease, TimeSpan.FromSeconds(10))).LeaseIsValid, Is.False);
        Assert.That(await backend.SaveCheckpointAsync(staleLease, "stale"), Is.False);
        Assert.That(await backend.CompleteAsync(staleLease, "stale"), Is.False);
        Assert.That(await backend.CompleteAsync(currentLease, "{\"ok\":true}"), Is.True);

        var record = await backend.GetAsync(submission.RunId);
        Assert.That(record!.Status, Is.EqualTo(AgentRunStatus.Succeeded));
        Assert.That(record.Attempt, Is.EqualTo(2));
        Assert.That(record.OutputJson, Is.EqualTo("{\"ok\":true}"));
    }

    [Test]
    public async Task Retry_waits_for_delay_then_exhausts_attempt_limit()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-11-03T12:00:00Z"));
        var backend = new InMemoryAgentRunBackend(clock);
        var submission = await backend.SubmitAsync(new AgentRunRequest("handler", "{}", MaxAttempts: 2));
        var firstLease = await backend.ClaimAsync("worker-a", TimeSpan.FromSeconds(30));

        Assert.That(await backend.RetryAsync(
            firstLease!,
            new AgentRunFailure("temporary", "Temporary failure.", Retryable: true),
            TimeSpan.FromSeconds(5)), Is.True);
        Assert.That((await backend.GetAsync(submission.RunId))!.Status, Is.EqualTo(AgentRunStatus.RetryScheduled));
        Assert.That(await backend.ClaimAsync("worker-a", TimeSpan.FromSeconds(30)), Is.Null);

        clock.Advance(TimeSpan.FromSeconds(5));
        var secondLease = await backend.ClaimAsync("worker-b", TimeSpan.FromSeconds(30));
        Assert.That(secondLease!.Attempt, Is.EqualTo(2));
        Assert.That(await backend.RetryAsync(
            secondLease,
            new AgentRunFailure("temporary", "Still failing.", Retryable: true),
            TimeSpan.Zero), Is.True);

        var record = await backend.GetAsync(submission.RunId);
        Assert.That(record!.Status, Is.EqualTo(AgentRunStatus.Failed));
        Assert.That(record.FailureCount, Is.EqualTo(2));
        Assert.That(record.FailureCode, Is.EqualTo("temporary"));
    }

    [Test]
    public async Task Resume_input_is_available_to_handler_then_cleared_when_it_waits_again()
    {
        var backend = new InMemoryAgentRunBackend();
        var submission = await backend.SubmitAsync(new AgentRunRequest("handler", "{}"));
        var firstLease = await backend.ClaimAsync("worker-a", TimeSpan.FromSeconds(30));
        Assert.That(await backend.WaitForInputAsync(firstLease!, "checkpoint-1", "First reply"), Is.True);
        Assert.That(await backend.ResumeAsync(submission.RunId, "\"first reply\""), Is.True);

        var resumedLease = await backend.ClaimAsync("worker-a", TimeSpan.FromSeconds(30));
        Assert.That(resumedLease!.ResumeInputJson, Is.EqualTo("\"first reply\""));
        Assert.That(await backend.WaitForInputAsync(resumedLease, "checkpoint-2", "Second reply"), Is.True);

        var record = await backend.GetAsync(submission.RunId);
        Assert.That(record!.Status, Is.EqualTo(AgentRunStatus.WaitingForInput));
        Assert.That(record.ResumeInputJson, Is.Null);
        Assert.That(record.CheckpointJson, Is.EqualTo("checkpoint-2"));
    }

    [Test]
    public async Task Cancellation_of_leased_run_is_signaled_to_worker_and_becomes_terminal_on_acknowledgement()
    {
        var backend = new InMemoryAgentRunBackend();
        var submission = await backend.SubmitAsync(new AgentRunRequest("handler", "{}"));
        var lease = await backend.ClaimAsync("worker-a", TimeSpan.FromSeconds(30));

        Assert.That(await backend.CancelAsync(submission.RunId), Is.True);
        var renewal = await backend.RenewLeaseAsync(lease!, TimeSpan.FromSeconds(30));
        Assert.That(renewal.LeaseIsValid, Is.True);
        Assert.That(renewal.CancellationRequested, Is.True);
        Assert.That(await backend.CompleteAsync(lease!), Is.True);
        Assert.That((await backend.GetAsync(submission.RunId))!.Status, Is.EqualTo(AgentRunStatus.Cancelled));
    }

    [Test]
    public async Task Deadline_expires_while_waiting_for_input()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-11-03T12:00:00Z"));
        var backend = new InMemoryAgentRunBackend(clock);
        var submission = await backend.SubmitAsync(new AgentRunRequest(
            "handler",
            "{}",
            Deadline: clock.GetUtcNow().AddMinutes(1)));
        var lease = await backend.ClaimAsync("worker-a", TimeSpan.FromSeconds(30));
        Assert.That(await backend.WaitForInputAsync(lease!, "checkpoint", "Reply required"), Is.True);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.That(await backend.ClaimAsync("worker-b", TimeSpan.FromSeconds(30)), Is.Null);
        Assert.That((await backend.GetAsync(submission.RunId))!.Status, Is.EqualTo(AgentRunStatus.DeadlineExceeded));
    }

    [Test]
    public async Task Per_scope_limits_reject_excess_submissions_and_skip_full_scopes_during_claim()
    {
        var backend = new InMemoryAgentRunBackend(
            timeProvider: null,
            options: new AgentRunBackendOptions { MaxOutstandingRunsPerScope = 2, MaxLeasedRunsPerScope = 1 });
        var first = new AgentRunRequest("handler", "{}", IdempotencyKey: "same", ScopeKey: "scope-a");
        var firstSubmission = await backend.SubmitAsync(first);
        var duplicate = await backend.SubmitAsync(first);
        var secondInScope = await backend.SubmitAsync(new AgentRunRequest("handler", "{}", ScopeKey: "scope-a"));
        await backend.SubmitAsync(new AgentRunRequest("handler", "{}", ScopeKey: "scope-b"));

        Assert.That(duplicate.RunId, Is.EqualTo(firstSubmission.RunId));
        Assert.ThrowsAsync<AgentRunAdmissionRejectedException>(async () =>
            await backend.SubmitAsync(new AgentRunRequest("handler", "{}", ScopeKey: "scope-a")));

        var leaseA = await backend.ClaimAsync("worker-a", TimeSpan.FromSeconds(30));
        var leaseB = await backend.ClaimAsync("worker-b", TimeSpan.FromSeconds(30));
        Assert.That(leaseA!.Request.ScopeKey, Is.EqualTo("scope-a"));
        Assert.That(leaseB!.Request.ScopeKey, Is.EqualTo("scope-b"));
        Assert.That(await backend.CompleteAsync(leaseA), Is.True);
        var nextLeaseA = await backend.ClaimAsync("worker-c", TimeSpan.FromSeconds(30));
        Assert.That(nextLeaseA!.RunId, Is.EqualTo(secondInScope.RunId));
    }

    [Test]
    public async Task Batch_submitter_tags_cases_and_reporter_summarizes_outcomes()
    {
        var backend = new InMemoryAgentRunBackend();
        var submitter = new AgentRunBatchSubmitter(backend);
        var submissions = new List<AgentRunSubmission>();
        await foreach (var submission in submitter.SubmitAsync("batch-17", CreateCases()))
            submissions.Add(submission);

        var cases = await backend.GetBatchRunsAsync("batch-17");
        Assert.That(cases.Select(static record => record.Request.CaseId), Is.EquivalentTo(new[] { "case-1", "case-2" }));
        Assert.That(cases.All(static record => record.Request.BatchId == "batch-17"), Is.True);
        var caseOneRunId = cases.Single(static record => record.Request.CaseId == "case-1").RunId;

        var successfulLease = await backend.ClaimAsync("worker-a", TimeSpan.FromSeconds(30));
        Assert.That(await backend.CompleteAsync(successfulLease!, "{\"ok\":true}"), Is.True);
        var failedLease = await backend.ClaimAsync("worker-a", TimeSpan.FromSeconds(30));
        Assert.That(await backend.FailAsync(
            failedLease!,
            new AgentRunFailure("invalid_case", "The case input was invalid.", Retryable: false)), Is.True);

        var traceSink = new InMemoryRunTraceSink();
        await traceSink.RecordAsync(new RunTraceRecord(
            caseOneRunId,
            "session-1",
            RunEventKind.TurnStarted,
            DateTimeOffset.UtcNow,
            PayloadJson: "{\"private\":\"value\"}"));
        await traceSink.RecordAsync(new RunTraceRecord(
            caseOneRunId,
            "session-1",
            RunEventKind.ToolCompleted,
            DateTimeOffset.UtcNow));
        var summary = await new AgentRunBatchReporter(backend, traceSink).GetSummaryAsync("batch-17");
        Assert.That(summary.RunCount, Is.EqualTo(2));
        Assert.That(summary.SucceededCount, Is.EqualTo(1));
        Assert.That(summary.FailedCount, Is.EqualTo(1));
        Assert.That(summary.PendingCount, Is.Zero);
        Assert.That(summary.IsComplete, Is.True);
        Assert.That(summary.SuccessRate, Is.EqualTo(0.5));
        Assert.That(submissions.Count, Is.EqualTo(2));
        Assert.That(summary.TraceSummaries, Has.Count.EqualTo(2));
        var traceSummary = summary.TraceSummaries!.Single(item => item.RunId == caseOneRunId);
        Assert.That(traceSummary.RecordCount, Is.EqualTo(2));
        Assert.That(traceSummary.EventCounts[RunEventKind.TurnStarted], Is.EqualTo(1));
        Assert.That(summary.TraceSummaries!.Single(item => item.RunId != caseOneRunId).RecordCount, Is.Zero);
    }

    [Test]
    public async Task Batch_cancellation_marks_queued_runs_and_requests_cancellation_for_leased_runs()
    {
        var backend = new InMemoryAgentRunBackend();
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

    private static async IAsyncEnumerable<AgentRunBatchCase> CreateCases()
    {
        yield return new AgentRunBatchCase("case-1", new AgentRunRequest("handler", "{\"value\":1}"));
        await Task.Yield();
        yield return new AgentRunBatchCase("case-2", new AgentRunRequest("handler", "{\"value\":2}"));
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private DateTimeOffset _now = initialTime;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
