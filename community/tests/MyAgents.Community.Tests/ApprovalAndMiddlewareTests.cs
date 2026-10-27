using Microsoft.Extensions.DependencyInjection;
using MyAgents.Core;
using MyAgents.Testing;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class ApprovalAndMiddlewareTests
{
    [Test]
    public async Task ToolApprovalEvaluator_executes_full_pipeline_including_gate_session_memory_and_audit()
    {
        var sessionMemory = new InMemorySessionApprovalMemory();
        var approvalStore = new InMemoryToolApprovalStore();
        var auditSink = new InMemoryToolApprovalAuditSink();

        // Policy returns Pending for sensitive tools
        var policy = new RuleBasedToolApprovalPolicy()
            .RequireApprovalForTool("deploy_db");

        // Interactive Gate approves with SessionCall scope
        var gate = new CallbackToolApprovalGate((req, dec, ct) =>
            ValueTask.FromResult(ToolApprovalDecision.Approve(decidedBy: "admin", reason: "approved in session", scope: ApprovalScope.SessionCall)));

        var request1 = new ToolApprovalRequest(
            ApprovalId: "app-1",
            SessionId: "session-42",
            ToolName: "deploy_db",
            ArgumentsJson: "{\"env\":\"prod\"}",
            RequestedAt: DateTimeOffset.UtcNow);

        // First evaluation: Policy returns Pending, Gate approves with SessionCall scope
        var decision1 = await ToolApprovalEvaluator.EvaluateAsync(
            request1,
            sessionMemory,
            approvalStore,
            policy,
            gate,
            auditSink);

        var allRecords = await auditSink.ListAllAsync();
        Assert.Multiple(() =>
        {
            Assert.That(decision1.Status, Is.EqualTo(ToolApprovalStatus.Approved));
            Assert.That(decision1.Scope, Is.EqualTo(ApprovalScope.SessionCall));
            Assert.That(allRecords, Has.Count.EqualTo(1));
            Assert.That(allRecords[0].ToolName, Is.EqualTo("deploy_db"));
        });

        // Verify stored in persistent store
        var stored = await approvalStore.GetAsync("app-1");
        Assert.That(stored, Is.Not.Null);
        Assert.That(stored!.Decision.Status, Is.EqualTo(ToolApprovalStatus.Approved));

        // Second evaluation with different approval ID but same session & tool & arguments:
        // should be resolved immediately from SessionApprovalMemory without calling policy or gate!
        var request2 = new ToolApprovalRequest(
            ApprovalId: "app-2",
            SessionId: "session-42",
            ToolName: "deploy_db",
            ArgumentsJson: "{\"env\":\"prod\"}",
            RequestedAt: DateTimeOffset.UtcNow);

        var decision2 = await ToolApprovalEvaluator.EvaluateAsync(
            request2,
            sessionMemory,
            approvalStore: null,
            policy: null,
            gate: null,
            auditSink);

        var finalRecords = await auditSink.ListAllAsync();
        Assert.Multiple(() =>
        {
            Assert.That(decision2.Status, Is.EqualTo(ToolApprovalStatus.Approved));
            Assert.That(finalRecords, Has.Count.EqualTo(2));
            Assert.That(finalRecords[1].GateType, Is.EqualTo("SessionApprovalMemory"));
        });
    }

    [Test]
    public async Task CircuitBreakerMiddleware_opens_after_threshold_and_blocks_further_calls()
    {
        var cb = new CircuitBreakerMiddleware(failureThreshold: 2, openDuration: TimeSpan.FromSeconds(60));
        var session = FakeAgentSession.CreateWithMiddleware("cb-session", cb);
        session.WithFallbackResponse("ok");

        var failingTurn = new Func<Task>(async () =>
        {
            var context = new AgentTurnContext("cb-session", [], new AgentTurnServices(new ServiceCollection().BuildServiceProvider()));
            await cb.InvokeAsync(context, (_, _) => throw new HttpRequestException("transient error"));
        });

        // 1st transient failure
        Assert.ThrowsAsync<HttpRequestException>(async () => await failingTurn());
        Assert.That(cb.State, Is.EqualTo(CircuitState.Closed));

        // 2nd transient failure -> trips to Open
        Assert.ThrowsAsync<HttpRequestException>(async () => await failingTurn());
        Assert.That(cb.State, Is.EqualTo(CircuitState.Open));

        // 3rd call immediately fails with CircuitBreakerOpenException without invoking inner handler
        Assert.ThrowsAsync<CircuitBreakerOpenException>(async () => await failingTurn());

        // Manual reset closes circuit
        cb.Reset();
        Assert.That(cb.State, Is.EqualTo(CircuitState.Closed));
    }

    [Test]
    public async Task ContextWindowMiddleware_trims_non_system_messages_retaining_system_prompts()
    {
        var trimmer = new SlidingWindowTrimmer(maxMessages: 2);
        var middleware = new ContextWindowMiddleware(trimmer);

        var messages = new List<AgentMessage>
        {
            AgentMessage.FromText("system", "You are a helpful assistant."),
            AgentMessage.FromText("user", "Message 1"),
            AgentMessage.FromText("assistant", "Message 2"),
            AgentMessage.FromText("user", "Message 3"),
            AgentMessage.FromText("assistant", "Message 4")
        };

        AgentTurnContext? capturedContext = null;
        var context = new AgentTurnContext("ctx-session", messages, new AgentTurnServices(new ServiceCollection().BuildServiceProvider()));

        await middleware.InvokeAsync(context, (ctx, _) =>
        {
            capturedContext = ctx;
            return ValueTask.FromResult(new AgentTurnResult([], new AgentTurnTelemetry(TimeSpan.Zero)));
        });

        Assert.That(capturedContext, Is.Not.Null);
        var effective = capturedContext!.Messages;

        Assert.Multiple(() =>
        {
            // System message kept + last 2 messages kept (Message 3, Message 4)
            Assert.That(effective, Has.Count.EqualTo(3));
            Assert.That(effective[0].Role, Is.EqualTo("system"));
            Assert.That(effective[0].Content, Is.EqualTo("You are a helpful assistant."));
            Assert.That(effective[1].Content, Is.EqualTo("Message 3"));
            Assert.That(effective[2].Content, Is.EqualTo("Message 4"));
        });
    }
}
