using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MyAgents.Core;
using MyAgents.Core.Messaging;
using MyAgents.Core.Prompts;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class ResilienceAndCoreUtilitiesTests
{
    private static AgentTurnContext CreateContext()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new AgentTurnContext(
            SessionId: "session-resilience-1",
            Messages: [new AgentMessage("user", "Hello world")],
            Services: new AgentTurnServices(services));
    }

    [Test]
    public async Task ResilienceMiddleware_RetriesOnTransientError_AndEventuallySucceeds()
    {
        var attempts = 0;
        var middleware = new ResilienceMiddleware(
            maxRetries: 2,
            baseDelay: TimeSpan.FromMilliseconds(5));

        var context = CreateContext();

        var result = await middleware.InvokeAsync(context, (ctx, ct) =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new HttpRequestException("Temporary 503 service unavailable");
            }

            return ValueTask.FromResult(new AgentTurnResult(
                [new AgentMessage("assistant", "Recovered output")],
                new AgentTurnTelemetry(TimeSpan.FromMilliseconds(5))));
        });

        Assert.That(attempts, Is.EqualTo(2));
        Assert.That(result.Output[0].Content, Is.EqualTo("Recovered output"));
    }

    [Test]
    public void ResilienceMiddleware_ExhaustsRetries_ThrowsWhenMaxRetriesExceeded()
    {
        var attempts = 0;
        var middleware = new ResilienceMiddleware(
            maxRetries: 1,
            baseDelay: TimeSpan.FromMilliseconds(5));

        var context = CreateContext();

        Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await middleware.InvokeAsync(context, (ctx, ct) =>
            {
                attempts++;
                throw new TimeoutException("Gateway timeout");
            });
        });

        Assert.That(attempts, Is.EqualTo(2)); // initial attempt + 1 retry
    }

    [Test]
    public async Task FallbackMiddleware_CatchesException_InvokesStaticFallbackPolicy()
    {
        var fallbackPolicy = new StaticFallbackPolicy("Backup response");
        var middleware = new FallbackMiddleware(fallbackPolicy);
        var context = CreateContext();

        var result = await middleware.InvokeAsync(context, (ctx, ct) =>
        {
            throw new InvalidOperationException("Fatal backend error");
        });

        Assert.That(result.Output.Count, Is.EqualTo(1));
        Assert.That(result.Output[0].Content, Is.EqualTo("Backup response"));
    }

    [Test]
    public async Task FallbackMiddleware_DeadLetterPolicy_EnqueuesFailedTurn()
    {
        var deadLetterQueue = new InMemoryDeadLetterQueue();
        var fallbackPolicy = new DeadLetterFallbackPolicy(deadLetterQueue, "Dead-letter handled");
        var middleware = new FallbackMiddleware(fallbackPolicy);
        var context = CreateContext();

        var result = await middleware.InvokeAsync(context, (ctx, ct) =>
        {
            throw new InvalidOperationException("Dead letter test error");
        });

        Assert.That(result.Output[0].Content, Is.EqualTo("Dead-letter handled"));
        Assert.That(deadLetterQueue.Items.Count, Is.EqualTo(1));
        var letter = deadLetterQueue.Items.First();
        Assert.That(letter.SessionId, Is.EqualTo(context.SessionId));
        Assert.That(letter.Exception.Message, Is.EqualTo("Dead letter test error"));
    }

    [Test]
    public async Task TokenBudgetTrimmer_DropsOldestNonSystemMessages_PreservesSystemMessage()
    {
        // CharacterTokenCounter counts ceil(length / 4.0)
        // System = "Sys" (3 chars = 1 token)
        // Old = "Long old question" (17 chars = 5 tokens)
        // New = "Newest" (6 chars = 2 tokens)
        // Total = 8 tokens.
        // With maxTokens = 4:
        // System (1) + New (2) = 3 tokens <= 4. "Long old question" is dropped.
        var trimmer = new TokenBudgetTrimmer(maxTokens: 4);

        var messages = new List<AgentMessage>
        {
            new("system", "Sys"),
            new("user", "Long old question"),
            new("user", "Newest")
        };

        var trimmed = await trimmer.TrimAsync(messages);

        Assert.That(trimmed.Any(m => m.Role == "system"), Is.True);
        Assert.That(trimmed.Any(m => m.Content == "Long old question"), Is.False);
        Assert.That(trimmed.Any(m => m.Content == "Newest"), Is.True);
    }

    [Test]
    public async Task AsyncSignalApprovalGate_ResolvesWhenExternalSignalSubmitted()
    {
        var gate = new AsyncSignalApprovalGate(TimeSpan.FromSeconds(5));

        var request = new ToolApprovalRequest(
            ApprovalId: "appr-123",
            SessionId: "session-gate-1",
            ToolName: "drop_database",
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow);

        var pendingDecision = ToolApprovalDecision.Pending("Waiting for admin approval");

        var waitTask = Task.Run(async () => await gate.EvaluateOrPromptAsync(request, pendingDecision));

        await Task.Delay(50);
        Assert.That(gate.PendingRequests.Count, Is.EqualTo(1));
        Assert.That(gate.PendingRequests[0].ApprovalId, Is.EqualTo("appr-123"));

        var submitted = gate.TrySubmitDecision("appr-123", ToolApprovalDecision.Approve("admin", "Confirmed via Slack"));
        Assert.That(submitted, Is.True);

        var finalDecision = await waitTask;
        Assert.That(finalDecision.Status, Is.EqualTo(ToolApprovalStatus.Approved));
        Assert.That(finalDecision.DecidedBy, Is.EqualTo("admin"));
        Assert.That(finalDecision.Reason, Is.EqualTo("Confirmed via Slack"));
        Assert.That(gate.PendingRequests.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task AsyncSignalApprovalGate_TimesOut_ReturnsPendingDecision()
    {
        var gate = new AsyncSignalApprovalGate(TimeSpan.FromMilliseconds(50));

        var request = new ToolApprovalRequest(
            ApprovalId: "appr-timeout",
            SessionId: "session-gate-2",
            ToolName: "restart_server",
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow);

        var pendingDecision = ToolApprovalDecision.Pending("Waiting for timeout");

        var finalDecision = await gate.EvaluateOrPromptAsync(request, pendingDecision);

        Assert.That(finalDecision.Status, Is.EqualTo(ToolApprovalStatus.Pending));
        Assert.That(finalDecision.Reason, Is.EqualTo("Waiting for timeout"));
    }

    [Test]
    public async Task AgentChannel_PublishAndSubscribe_BroadcastsEnvelopes()
    {
        using var channel = new InMemoryAgentChannel();
        await using var subscription = channel.Subscribe();

        var envelope = new ChannelEnvelope(
            Message: new AgentMessage("user", "Channel payload"),
            SenderAgentName: "agent-publisher",
            PublishedAt: DateTimeOffset.UtcNow);

        await channel.PublishAsync(envelope);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var received = new List<ChannelEnvelope>();
        await foreach (var env in subscription.ReadAllAsync(cts.Token))
        {
            received.Add(env);
            break;
        }

        Assert.That(received.Count, Is.EqualTo(1));
        Assert.That(received[0].SenderAgentName, Is.EqualTo("agent-publisher"));
        Assert.That(received[0].Message.Content, Is.EqualTo("Channel payload"));
    }

    [Test]
    public void PromptRegistry_RegistersAndRendersTemplate()
    {
        var registry = new PromptRegistry();
        var def = new PromptTemplateDefinition(
            Name: "greeting",
            Version: "1.0",
            Template: "Hello {Name}, welcome to {City}!");

        registry.Register(def);

        var template = registry.GetLatest("greeting");
        Assert.That(template, Is.Not.Null);

        var rendered = template!.Render(new Dictionary<string, string>
        {
            ["Name"] = "Alice",
            ["City"] = "Seattle"
        });

        Assert.That(rendered, Is.EqualTo("Hello Alice, welcome to Seattle!"));
    }

    [Test]
    public async Task InMemoryRunTraceSink_RecordsAndListsTraces()
    {
        var sink = new InMemoryRunTraceSink();

        var record1 = new RunTraceRecord(
            RunId: "run-abc",
            SessionId: "sess-abc",
            EventKind: RunEventKind.TurnStarted,
            RecordedAt: DateTimeOffset.UtcNow.AddSeconds(-1));

        var record2 = new RunTraceRecord(
            RunId: "run-abc",
            SessionId: "sess-abc",
            EventKind: RunEventKind.TurnCompleted,
            RecordedAt: DateTimeOffset.UtcNow);

        await sink.RecordAsync(record1);
        await sink.RecordAsync(record2);

        var records = await sink.ListAsync("run-abc");
        Assert.That(records.Count, Is.EqualTo(2));
        Assert.That(records[0].EventKind, Is.EqualTo(RunEventKind.TurnStarted));
        Assert.That(records[1].EventKind, Is.EqualTo(RunEventKind.TurnCompleted));

        var runIds = await sink.ListRunIdsAsync();
        Assert.That(runIds, Is.Not.Null);
        Assert.That(runIds, Contains.Item("run-abc"));
    }
}
