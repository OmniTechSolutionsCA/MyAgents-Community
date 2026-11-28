using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using MyAgents.Agents;
using MyAgents.Core;
using MyAgents.Core.Execution;
using MyAgents.Core.Tools;
using MyAgents.Core.Workflows;
using MyAgents.Runtime.Chat;
using MyAgents.Runtime.Workers;
using MyAgents.Runtime.Workers.Sqlite;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class AsyncHitlSuspendResumeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    [Test]
    public void ApproveWithEdits_throws_when_modifiedArgumentsJson_is_null_or_whitespace()
    {
        Assert.Throws<ArgumentNullException>(() => ToolApprovalDecision.ApproveWithEdits(null!));
        Assert.Throws<ArgumentException>(() => ToolApprovalDecision.ApproveWithEdits("   "));
    }

    [Test]
    public void ApproveWithEdits_throws_JsonException_when_modifiedArgumentsJson_is_invalid_json()
    {
        Assert.Catch<JsonException>(() => ToolApprovalDecision.ApproveWithEdits("{invalid-json"));
    }

    [Test]
    public void ChatClientRuntime_rejects_tool_even_when_ModifiedArgumentsJson_is_populated()
    {
        var invoked = false;
        var decision = new ToolApprovalDecision(
            ToolApprovalStatus.Rejected,
            DateTimeOffset.UtcNow,
            DecidedBy: "dr-smith",
            Reason: "Contraindicated",
            ModifiedArgumentsJson: "{\"reply\":\"override\"}");

        var services = new ServiceCollection()
            .AddSingleton<IToolApprovalPolicy>(new FixedPolicy(decision))
            .BuildServiceProvider();

        var chatClient = new ScriptedToolChatClient("relay_reply", "{\"reply\":\"Take 500mg\"}");
        var runtime = new ChatClientRuntime(chatClient, Microsoft.Extensions.Options.Options.Create(new ChatClientRuntimeOptions()));
        var feature = new ToolsFeature().AddFunction(
            (string reply) =>
            {
                invoked = true;
                return "sent";
            },
            name: "relay_reply",
            description: "Relay reply");
        var definition = new AgentDefinition("medical-intern", [feature]);
        var factory = new DefaultAgentSessionFactory(runtime, services);

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var session = factory.CreateSession("session-reject-with-edits", definition);
            await session.InvokeAsync([AgentMessage.FromText("user", "Can I take this?")]);
        });
        Assert.That(invoked, Is.False);
    }

    [Test]
    public async Task ChatAgentRunHandler_preserves_message_history_across_chained_approvals()
    {
        var secondRequest = new ToolApprovalRequest("approval-b", "run-chain", "update_chart", "{\"note\":\"updated\"}", DateTimeOffset.UtcNow);
        var secondPending = new PendingToolApproval("approval-b", secondRequest, ToolApprovalDecision.Pending("Chart write requires approval"));

        var factory = new StubSessionFactory(_ => throw new ToolApprovalRequiredException(secondPending));
        var handler = CreateChatHandler(factory);

        var firstRequest = new ToolApprovalRequest("approval-a", "run-chain", "send_medical_advice", "{\"reply\":\"orig\"}", DateTimeOffset.UtcNow);
        var firstCheckpoint = new ChatAgentRunCheckpoint(
            [AgentMessage.FromText("user", "Update my chart and reply")],
            new PendingToolApproval("approval-a", firstRequest, ToolApprovalDecision.Pending()));

        var firstDecision = ToolApprovalDecision.ApproveWithEdits("{\"reply\":\"edited\"}", decidedBy: "dr-smith");
        var result = await handler.ExecuteAsync(CreateRunContext(
            "run-chain",
            "[]",
            checkpointJson: JsonSerializer.Serialize(firstCheckpoint, GetTypeInfo<ChatAgentRunCheckpoint>()),
            resumeInputJson: JsonSerializer.Serialize(firstDecision, GetTypeInfo<ToolApprovalDecision>())));

        Assert.That(result.Disposition, Is.EqualTo(AgentRunHandlerDisposition.WaitingForInput));
        var secondCheckpoint = JsonSerializer.Deserialize(result.CheckpointJson!, GetTypeInfo<ChatAgentRunCheckpoint>());
        Assert.Multiple(() =>
        {
            Assert.That(secondCheckpoint, Is.Not.Null);
            Assert.That(secondCheckpoint!.PendingToolApproval.ApprovalId, Is.EqualTo("approval-b"));
            Assert.That(secondCheckpoint.Messages, Has.Count.EqualTo(2));
            Assert.That(secondCheckpoint.Messages[0].Content, Is.EqualTo("Update my chart and reply"));
            Assert.That(secondCheckpoint.Messages[1].Content, Does.Contain("Approval granted by dr-smith"));
        });
    }

    [Test]
    public async Task ChatClientRuntime_executes_tool_with_ModifiedArgumentsJson()
    {
        string? invokedArguments = null;

        var policy = new FixedPolicy(ToolApprovalDecision.ApproveWithEdits(
            "{\"reply\":\"Take 250mg after food\"}",
            decidedBy: "dr-smith"));

        var services = new ServiceCollection()
            .AddSingleton<IToolApprovalPolicy>(policy)
            .BuildServiceProvider();

        var chatClient = new ScriptedToolChatClient("relay_reply", "{\"reply\":\"Take 500mg\"}");
        var runtime = new ChatClientRuntime(chatClient, Microsoft.Extensions.Options.Options.Create(new ChatClientRuntimeOptions()));
        var feature = new ToolsFeature().AddFunction(
            (string reply) =>
            {
                invokedArguments = reply;
                return "sent";
            },
            name: "relay_reply",
            description: "Relay reply");
        var definition = new AgentDefinition("medical-intern", [feature]);
        var factory = new DefaultAgentSessionFactory(runtime, services);
        await using var session = factory.CreateSession("session-edit-2", definition);

        var result = await session.InvokeAsync([AgentMessage.FromText("user", "How should I take this?")]);

        Assert.Multiple(() =>
        {
            Assert.That(invokedArguments, Is.EqualTo("Take 250mg after food"));
            Assert.That(result.Output, Is.Not.Empty);
        });
    }

    [Test]
    public async Task ChatAgentRunHandler_suspends_with_WaitingForInput_when_tool_requires_approval()
    {
        var request = new ToolApprovalRequest(
            "approval-1",
            "run-1",
            "send_medical_advice",
            "{\"reply\":\"Take 500mg\"}",
            DateTimeOffset.UtcNow);
        var pending = new PendingToolApproval("approval-1", request, ToolApprovalDecision.Pending("Doctor review required"));

        var factory = new StubSessionFactory(_ => throw new ToolApprovalRequiredException(pending));
        var handler = CreateChatHandler(factory);

        var inputJson = JsonSerializer.Serialize<IReadOnlyList<AgentMessage>>(
            [AgentMessage.FromText("user", "Can I double my dose?")],
            GetTypeInfo<IReadOnlyList<AgentMessage>>());

        var result = await handler.ExecuteAsync(CreateRunContext("run-1", inputJson));

        Assert.Multiple(() =>
        {
            Assert.That(result.Disposition, Is.EqualTo(AgentRunHandlerDisposition.WaitingForInput));
            Assert.That(result.PendingInputDescription, Is.Not.Null.And.Not.Empty);
            Assert.That(result.CheckpointJson, Is.Not.Null);
        });

        var checkpoint = JsonSerializer.Deserialize(result.CheckpointJson!, GetTypeInfo<ChatAgentRunCheckpoint>());
        Assert.That(checkpoint, Is.Not.Null);
        Assert.That(checkpoint!.PendingToolApproval.ApprovalId, Is.EqualTo("approval-1"));
        Assert.That(checkpoint.Messages, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ChatAgentRunHandler_resumes_with_approved_edited_decision_and_completes()
    {
        IReadOnlyList<AgentMessage>? resumedMessages = null;
        var factory = new StubSessionFactory(messages =>
        {
            resumedMessages = messages;
            return new AgentTurnResult(
                [AgentMessage.FromText("assistant", "Relayed doctor's approved reply.")],
                new AgentTurnTelemetry(TimeSpan.Zero));
        });
        var handler = CreateChatHandler(factory);

        var request = new ToolApprovalRequest("approval-2", "run-2", "send_medical_advice", "{\"reply\":\"orig\"}", DateTimeOffset.UtcNow);
        var checkpoint = new ChatAgentRunCheckpoint(
            [AgentMessage.FromText("user", "What should I do?")],
            new PendingToolApproval("approval-2", request, ToolApprovalDecision.Pending()));

        var decision = ToolApprovalDecision.ApproveWithEdits("{\"reply\":\"edited\"}", decidedBy: "dr-smith");
        var context = CreateRunContext(
            "run-2",
            "[]",
            checkpointJson: JsonSerializer.Serialize(checkpoint, GetTypeInfo<ChatAgentRunCheckpoint>()),
            resumeInputJson: JsonSerializer.Serialize(decision, GetTypeInfo<ToolApprovalDecision>()));

        var result = await handler.ExecuteAsync(context);

        Assert.Multiple(() =>
        {
            Assert.That(result.Disposition, Is.EqualTo(AgentRunHandlerDisposition.Completed));
            Assert.That(resumedMessages, Is.Not.Null);
            Assert.That(resumedMessages!, Has.Count.EqualTo(2));
            Assert.That(resumedMessages![1].Content, Does.Contain("Approval granted by dr-smith"));
            Assert.That(resumedMessages![1].Content, Does.Contain("{\"reply\":\"edited\"}"));
        });
    }

    [Test]
    public async Task ChatAgentRunHandler_resumes_with_rejected_decision_and_notifies_session()
    {
        IReadOnlyList<AgentMessage>? resumedMessages = null;
        var factory = new StubSessionFactory(messages =>
        {
            resumedMessages = messages;
            return new AgentTurnResult(
                [AgentMessage.FromText("assistant", "Please consult your doctor in person.")],
                new AgentTurnTelemetry(TimeSpan.Zero));
        });
        var handler = CreateChatHandler(factory);

        var request = new ToolApprovalRequest("approval-3", "run-3", "send_medical_advice", "{\"reply\":\"orig\"}", DateTimeOffset.UtcNow);
        var checkpoint = new ChatAgentRunCheckpoint(
            [AgentMessage.FromText("user", "Can I stop taking this?")],
            new PendingToolApproval("approval-3", request, ToolApprovalDecision.Pending()));

        var decision = ToolApprovalDecision.Reject("dr-smith", "Needs in-person evaluation");
        var context = CreateRunContext(
            "run-3",
            "[]",
            checkpointJson: JsonSerializer.Serialize(checkpoint, GetTypeInfo<ChatAgentRunCheckpoint>()),
            resumeInputJson: JsonSerializer.Serialize(decision, GetTypeInfo<ToolApprovalDecision>()));

        var result = await handler.ExecuteAsync(context);

        Assert.Multiple(() =>
        {
            Assert.That(result.Disposition, Is.EqualTo(AgentRunHandlerDisposition.Completed));
            Assert.That(resumedMessages![1].Content, Does.Contain("Approval rejected by dr-smith"));
            Assert.That(resumedMessages![1].Content, Does.Contain("Needs in-person evaluation"));
        });
    }

    [Test]
    public async Task WorkflowAgentRunHandler_resumes_with_structured_json_object_payload()
    {
        var workflow = new HumanInputEchoWorkflow();
        var host = new WorkflowHost(new StubSessionFactory(_ => new AgentTurnResult([], new AgentTurnTelemetry(TimeSpan.Zero))));
        var handler = CreateWorkflowHandler(host, workflow);

        var firstResult = await handler.ExecuteAsync(CreateRunContext(
            "wf-run-1",
            JsonSerializer.Serialize(new WorkflowEchoState(null), GetTypeInfo<WorkflowEchoState>())));

        Assert.That(firstResult.Disposition, Is.EqualTo(AgentRunHandlerDisposition.WaitingForInput));

        var structuredPayload = "{\"approved\":true,\"editedReply\":\"Take 250mg\",\"reviewedBy\":\"Dr. Smith\"}";
        var resumed = await handler.ExecuteAsync(CreateRunContext(
            "wf-run-1",
            "{}",
            checkpointJson: firstResult.CheckpointJson,
            resumeInputJson: structuredPayload));

        Assert.That(resumed.Disposition, Is.EqualTo(AgentRunHandlerDisposition.Completed));
        var finalState = JsonSerializer.Deserialize(resumed.OutputJson!, GetTypeInfo<WorkflowEchoState>());
        Assert.That(finalState?.Reply, Is.EqualTo(structuredPayload));
    }

    [Test]
    public async Task WorkflowAgentRunHandler_preserves_plain_json_string_resume_payload()
    {
        var workflow = new HumanInputEchoWorkflow();
        var host = new WorkflowHost(new StubSessionFactory(_ => new AgentTurnResult([], new AgentTurnTelemetry(TimeSpan.Zero))));
        var handler = CreateWorkflowHandler(host, workflow);

        var firstResult = await handler.ExecuteAsync(CreateRunContext(
            "wf-run-2",
            JsonSerializer.Serialize(new WorkflowEchoState(null), GetTypeInfo<WorkflowEchoState>())));

        var resumed = await handler.ExecuteAsync(CreateRunContext(
            "wf-run-2",
            "{}",
            checkpointJson: firstResult.CheckpointJson,
            resumeInputJson: "\"yes, proceed\""));

        Assert.That(resumed.Disposition, Is.EqualTo(AgentRunHandlerDisposition.Completed));
        var finalState = JsonSerializer.Deserialize(resumed.OutputJson!, GetTypeInfo<WorkflowEchoState>());
        Assert.That(finalState?.Reply, Is.EqualTo("yes, proceed"));
    }

    [Test]
    public async Task WorkflowAgentRunHandler_throws_on_invalid_json_resume_payload()
    {
        var workflow = new HumanInputEchoWorkflow();
        var host = new WorkflowHost(new StubSessionFactory(_ => new AgentTurnResult([], new AgentTurnTelemetry(TimeSpan.Zero))));
        var handler = CreateWorkflowHandler(host, workflow);

        var firstResult = await handler.ExecuteAsync(CreateRunContext(
            "wf-run-3",
            JsonSerializer.Serialize(new WorkflowEchoState(null), GetTypeInfo<WorkflowEchoState>())));

        Assert.CatchAsync<JsonException>(async () =>
            await handler.ExecuteAsync(CreateRunContext(
                "wf-run-3",
                "{}",
                checkpointJson: firstResult.CheckpointJson,
                resumeInputJson: "{invalid")));
    }

    [Test]
    public void AgentRunHandlerResult_WaitingForInput_persists_optional_ExpiresAt()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        var withTtl = AgentRunHandlerResult.WaitingForInput("{}", "Needs review", expiresAt);
        var withoutTtl = AgentRunHandlerResult.WaitingForInput("{}", "Needs review");

        Assert.Multiple(() =>
        {
            Assert.That(withTtl.PendingInputExpiresAt, Is.EqualTo(expiresAt));
            Assert.That(withoutTtl.PendingInputExpiresAt, Is.Null);
        });
    }

    [Test]
    public async Task SqliteSessionApprovalMemory_persists_SessionTool_and_SessionCall_grants_across_reopen()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"myagents-hitl-{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        try
        {
            var req1 = new ToolApprovalRequest("a1", "sess-1", "read_lab", "{\"id\":1}", DateTimeOffset.UtcNow);
            var req2 = new ToolApprovalRequest("a2", "sess-1", "send_advice", "{\"msg\":\"orig\"}", DateTimeOffset.UtcNow);

            using (var store = new SqliteSessionApprovalMemory(connStr))
            {
                await store.RecordDecisionAsync(req1, ToolApprovalDecision.Approve("dr-1", scope: ApprovalScope.SessionTool));
                await store.RecordDecisionAsync(req2, ToolApprovalDecision.ApproveWithEdits("{\"msg\":\"edited\"}", "dr-1", scope: ApprovalScope.SessionCall));
            }

            using var reopened = new SqliteSessionApprovalMemory(connStr);
            var granted1 = await reopened.TryGetGrantedDecisionAsync(
                new ToolApprovalRequest("a3", "sess-1", "read_lab", "{\"id\":99}", DateTimeOffset.UtcNow));
            var granted2 = await reopened.TryGetGrantedDecisionAsync(req2);

            Assert.Multiple(() =>
            {
                Assert.That(granted1, Is.Not.Null);
                Assert.That(granted1!.Status, Is.EqualTo(ToolApprovalStatus.Approved));
                Assert.That(granted2, Is.Not.Null);
                Assert.That(granted2!.ModifiedArgumentsJson, Is.EqualTo("{\"msg\":\"edited\"}"));
            });
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task SqliteSessionApprovalMemory_handles_concurrent_reads_and_writes()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"myagents-concurrent-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new SqliteSessionApprovalMemory($"Data Source={dbPath}");
            var tasks = Enumerable.Range(0, 20).Select(async i =>
            {
                var req = new ToolApprovalRequest($"a-{i}", $"sess-{i}", "read_lab", $"{{\"id\":{i}}}", DateTimeOffset.UtcNow);
                await store.RecordDecisionAsync(req, ToolApprovalDecision.Approve($"dr-{i}", scope: ApprovalScope.SessionTool));
                var granted = await store.TryGetGrantedDecisionAsync(req);
                Assert.That(granted, Is.Not.Null);
                Assert.That(granted!.DecidedBy, Is.EqualTo($"dr-{i}"));
            });

            await Task.WhenAll(tasks);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task SqliteSessionApprovalMemory_ignores_Once_and_Rejected_decisions()
    {
        using var store = new SqliteSessionApprovalMemory("Data Source=:memory:");
        var req = new ToolApprovalRequest("a1", "sess-1", "prescribe", "{\"dose\":\"500mg\"}", DateTimeOffset.UtcNow);

        await store.RecordDecisionAsync(req, ToolApprovalDecision.Approve("dr-1", scope: ApprovalScope.Once));
        await store.RecordDecisionAsync(req, ToolApprovalDecision.Reject("dr-1", "Unsafe"));

        var granted = await store.TryGetGrantedDecisionAsync(req);
        Assert.That(granted, Is.Null);
    }

    [Test]
    public async Task SqliteSessionApprovalMemory_ClearSessionAsync_removes_only_target_session()
    {
        using var store = new SqliteSessionApprovalMemory("Data Source=:memory:");
        var req1 = new ToolApprovalRequest("a1", "sess-1", "read_lab", "{}", DateTimeOffset.UtcNow);
        var req2 = new ToolApprovalRequest("a2", "sess-2", "read_lab", "{}", DateTimeOffset.UtcNow);

        await store.RecordDecisionAsync(req1, ToolApprovalDecision.Approve("dr-1", scope: ApprovalScope.SessionTool));
        await store.RecordDecisionAsync(req2, ToolApprovalDecision.Approve("dr-1", scope: ApprovalScope.SessionTool));

        await store.ClearSessionAsync("sess-1");

        Assert.Multiple(async () =>
        {
            Assert.That(await store.TryGetGrantedDecisionAsync(req1), Is.Null);
            Assert.That(await store.TryGetGrantedDecisionAsync(req2), Is.Not.Null);
        });
    }

    [Test]
    public async Task ToolApprovalEvaluator_records_both_original_and_modified_arguments_in_AuditSink()
    {
        ToolApprovalAuditRecord? captured = null;
        var sink = new CaptureAuditSink(r => captured = r);
        var policy = new FixedPolicy(ToolApprovalDecision.ApproveWithEdits("{\"dose\":\"250mg\"}", decidedBy: "dr-smith"));

        var services = new ServiceCollection()
            .AddSingleton<IToolApprovalPolicy>(policy)
            .AddSingleton<IToolApprovalAuditSink>(sink)
            .BuildServiceProvider();

        var req = new ToolApprovalRequest("a1", "sess-1", "prescribe", "{\"dose\":\"500mg\"}", DateTimeOffset.UtcNow);
        var decision = await ToolApprovalEvaluator.EvaluateAsync(req, services);

        Assert.Multiple(() =>
        {
            Assert.That(decision.ModifiedArgumentsJson, Is.EqualTo("{\"dose\":\"250mg\"}"));
            Assert.That(captured, Is.Not.Null);
            Assert.That(captured!.ArgumentsJson, Is.EqualTo("{\"dose\":\"500mg\"}"));
            Assert.That(captured.Decision.ModifiedArgumentsJson, Is.EqualTo("{\"dose\":\"250mg\"}"));
        });
    }

    [Test]
    public async Task SqliteAgentRunBackend_ExpireWaitingRunsAsync_transitions_expired_waiting_runs_to_DeadlineExceeded()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"myagents-expire-{Guid.NewGuid():N}.db");
        try
        {
            var backend = new SqliteAgentRunBackend(dbPath);
            var now = DateTimeOffset.UtcNow;

            var submission = await backend.SubmitAsync(new AgentRunRequest("handler", "{}", Deadline: now.AddMinutes(5)));
            var lease = await backend.ClaimAsync("worker-1", TimeSpan.FromMinutes(1));
            Assert.That(lease, Is.Not.Null);

            await backend.WaitForInputAsync(lease!, "{}", "Needs doctor review");

            var expiredCount = await backend.ExpireWaitingRunsAsync(now.AddMinutes(10));
            var record = await backend.GetAsync(submission.RunId);

            Assert.Multiple(() =>
            {
                Assert.That(expiredCount, Is.EqualTo(1));
                Assert.That(record, Is.Not.Null);
                Assert.That(record!.Status, Is.EqualTo(AgentRunStatus.DeadlineExceeded));
            });
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    private static AgentRunContext CreateRunContext(
        string runId,
        string inputJson,
        string? checkpointJson = null,
        string? resumeInputJson = null)
    {
        var request = new AgentRunRequest("handler", inputJson);
        var lease = new AgentRunLease(
            runId,
            request,
            "worker-1",
            "lease-1",
            1,
            1,
            0,
            DateTimeOffset.UtcNow.AddMinutes(5),
            checkpointJson,
            resumeInputJson);
        return new AgentRunContext(lease, (_, _) => ValueTask.FromResult(true));
    }

    private static ChatAgentRunHandler CreateChatHandler(IAgentSessionFactory factory)
        => new(
            "chat-hitl",
            factory,
            new AgentDefinition("medical-intern", []),
            GetTypeInfo<IReadOnlyList<AgentMessage>>(),
            GetTypeInfo<ChatAgentRunCheckpoint>(),
            GetTypeInfo<ToolApprovalDecision>(),
            GetTypeInfo<AgentTurnResult>());

    private static WorkflowAgentRunHandler<WorkflowEchoState> CreateWorkflowHandler(
        WorkflowHost host,
        IWorkflow<WorkflowEchoState> workflow)
        => new(
            "wf-hitl",
            host,
            workflow,
            GetTypeInfo<WorkflowEchoState>(),
            GetTypeInfo<WorkflowSnapshot>(),
            GetTypeInfo<ToolApprovalDecision>());

    private static JsonTypeInfo<T> GetTypeInfo<T>()
        => (JsonTypeInfo<T>)JsonOptions.GetTypeInfo(typeof(T));

    private sealed record WorkflowEchoState(string? Reply);

    private sealed class HumanInputEchoWorkflow : IWorkflow<WorkflowEchoState>
    {
        public string Id => "human-input-echo";
        public int StateVersion => 1;

        public ValueTask RunAsync(
            IWorkflowSession<WorkflowEchoState> session,
            CancellationToken cancellationToken = default)
        {
            var pending = session.GetPendingHumanInput();
            if (pending?.Reply is null)
            {
                throw new HumanInputRequiredException(
                    session.CreateSnapshot() with { PendingHumanInput = new HumanInputRequest("ask", "Provide review") });
            }

            session.State = new WorkflowEchoState(pending.Reply);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CaptureTool(string name, Func<string, string> onInvoke) : ITool
    {
        public string Name => name;
        public string Description => "Test capture tool";
        public string InputSchemaJson => "{}";

        public ValueTask<string> InvokeAsync(string argumentsJson, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(onInvoke(argumentsJson));
    }

    private sealed class FixedPolicy(ToolApprovalDecision decision) : IToolApprovalPolicy
    {
        public ValueTask<ToolApprovalDecision> DecideAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(decision);
    }

    private sealed class CaptureAuditSink(Action<ToolApprovalAuditRecord> onRecord) : IToolApprovalAuditSink
    {
        public ValueTask RecordAsync(ToolApprovalAuditRecord record, CancellationToken cancellationToken = default)
        {
            onRecord(record);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<ToolApprovalAuditRecord>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<ToolApprovalAuditRecord>>([]);

        public ValueTask<IReadOnlyList<ToolApprovalAuditRecord>> ListAllAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<ToolApprovalAuditRecord>>([]);
    }

    private sealed class StubSessionFactory(Func<IReadOnlyList<AgentMessage>, AgentTurnResult> onInvoke) : IAgentSessionFactory
    {
        public IAgentSession CreateSession(string sessionId, AgentDefinition definition)
            => new StubSession(sessionId, onInvoke);

        private sealed class StubSession(string sessionId, Func<IReadOnlyList<AgentMessage>, AgentTurnResult> onInvoke) : IAgentSession
        {
            public string SessionId => sessionId;
            public IReadOnlyList<IAgentCapability> Capabilities => [];

            public ValueTask<AgentTurnResult> InvokeAsync(IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
                => ValueTask.FromResult(onInvoke(messages));

            public ValueTask<AgentTurnResult> InvokeAsync(
                IReadOnlyList<AgentMessage> messages,
                Action<AgentTurnContext>? configureContext,
                CancellationToken cancellationToken = default)
                => ValueTask.FromResult(onInvoke(messages));

            public IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
                IReadOnlyList<AgentMessage> messages,
                CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedToolChatClient(string toolName, string argumentsJson) : IChatClient
    {
        private int _turn;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _turn++;
            if (_turn == 1)
            {
                var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson);
                var call = new FunctionCallContent("call-1", toolName, args);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
