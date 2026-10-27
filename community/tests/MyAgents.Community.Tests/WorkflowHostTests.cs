using Microsoft.Extensions.DependencyInjection;
using MyAgents.Core;
using MyAgents.Core.Workflows;
using MyAgents.Testing;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class WorkflowHostTests
{
    private record PipelineState(string Step = "", string HumanReply = "", bool ToolApproved = false)
    {
        public List<string> ResultList { get; init; } = [];
    }

    private sealed class HumanInTheLoopWorkflow : IWorkflow<PipelineState>
    {
        public string Id => "hitl-workflow";
        public int StateVersion => 1;

        public async ValueTask RunAsync(IWorkflowSession<PipelineState> session, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(session.State.Step))
            {
                session.State = session.State with { Step = "initial" };
                // Pause for human confirmation
                await WorkflowSteps.RequestHumanInputAsync(session, "ask-confirmation", "Do you approve proceeding?", cancellationToken);
            }

            var input = session.GetPendingHumanInput();
            if (input is not null)
            {
                session.State = session.State with
                {
                    Step = "human-received",
                    HumanReply = input.Reply ?? string.Empty
                };
            }
        }
    }

    private sealed class ToolApprovalWorkflow : IWorkflow<PipelineState>
    {
        public string Id => "tool-approval-workflow";
        public int StateVersion => 1;

        public async ValueTask RunAsync(IWorkflowSession<PipelineState> session, CancellationToken cancellationToken = default)
        {
            if (session is IWorkflowApprovalContext approvalContext &&
                approvalContext.GetPendingToolApproval()?.Decision.Status == ToolApprovalStatus.Approved)
            {
                session.State = session.State with { ToolApproved = true };
                return;
            }

            if (!session.State.ToolApproved)
            {
                var request = new ToolApprovalRequest(
                    "req-1",
                    session.SessionId,
                    "deploy_prod",
                    "{}",
                    DateTimeOffset.UtcNow);

                var pending = new PendingToolApproval("req-1", request, ToolApprovalDecision.Pending("admin approval needed"));
                await WorkflowSteps.RequestToolApprovalAsync(session, pending, cancellationToken);
            }
        }
    }

    private sealed class MultiStepAgentWorkflow : IWorkflow<PipelineState>
    {
        public string Id => "agent-steps-workflow";
        public int StateVersion => 1;

        public async ValueTask RunAsync(IWorkflowSession<PipelineState> session, CancellationToken cancellationToken = default)
        {
            // Sequential
            var seqResult = await WorkflowSteps.RunSequentialAsync(
                session,
                ["agent1", "agent2"],
                [AgentMessage.FromText("user", "start")]);
            session.State.ResultList.Add($"seq:{seqResult.Output[0].Content}");

            // Concurrent
            var concResults = await WorkflowSteps.RunConcurrentAsync(
                session,
                ["agent1", "agent2"],
                [AgentMessage.FromText("user", "ping")]);
            foreach (var r in concResults)
                session.State.ResultList.Add($"conc:{r.Output[0].Content}");

            // Handoff
            var handoffResult = await WorkflowSteps.RunHandoffAsync(
                session,
                firstAgentName: "agent1",
                initialMessages: [AgentMessage.FromText("user", "handoff")],
                nextAgentSelector: (res, hops) => hops == 1 ? "agent2" : null);
            session.State.ResultList.Add($"handoff:{handoffResult.Output[0].Content}");
        }
    }

    [Test]
    public async Task WorkflowHost_pauses_for_human_input_and_resumes_with_reply()
    {
        var runtime = new InMemoryAgentRuntime();
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new DefaultAgentSessionFactory(runtime, services);
        var host = new WorkflowHost(factory);

        var workflow = new HumanInTheLoopWorkflow();
        var initialState = new PipelineState();

        WorkflowSnapshot? snapshot = null;
        try
        {
            await host.RunAsync(workflow, initialState);
            Assert.Fail("Workflow should have paused for human input.");
        }
        catch (HumanInputRequiredException ex)
        {
            snapshot = ex.Snapshot;
        }

        Assert.That(snapshot, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot!.WorkflowId, Is.EqualTo("hitl-workflow"));
            Assert.That(snapshot.PendingHumanInput?.StepId, Is.EqualTo("ask-confirmation"));
            Assert.That(snapshot.PendingHumanInput?.Prompt, Is.EqualTo("Do you approve proceeding?"));
        });

        // Resume with human input
        var finalState = await host.ResumeWithInputAsync(workflow, snapshot!, "Yes, approved!");
        Assert.Multiple(() =>
        {
            Assert.That(finalState.Step, Is.EqualTo("human-received"));
            Assert.That(finalState.HumanReply, Is.EqualTo("Yes, approved!"));
        });
    }

    [Test]
    public async Task WorkflowHost_pauses_for_tool_approval_and_resumes_with_decision()
    {
        var runtime = new InMemoryAgentRuntime();
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new DefaultAgentSessionFactory(runtime, services);
        var host = new WorkflowHost(factory);

        var workflow = new ToolApprovalWorkflow();
        WorkflowSnapshot? snapshot = null;
        try
        {
            await host.RunAsync(workflow, new PipelineState());
            Assert.Fail("Workflow should have paused for tool approval.");
        }
        catch (HumanInputRequiredException ex)
        {
            snapshot = ex.Snapshot;
        }

        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot!.PendingToolApproval?.ApprovalId, Is.EqualTo("req-1"));

        var resumedState = await host.ResumeWithToolApprovalAsync(
            workflow,
            snapshot,
            ToolApprovalDecision.Approve("operator authorized"));

        Assert.That(resumedState, Is.Not.Null);
        Assert.That(resumedState.ToolApproved, Is.True);
    }

    [Test]
    public async Task WorkflowSteps_RunSequential_RunConcurrent_and_RunHandoff_work_cleanly()
    {
        var runtime = new InMemoryAgentRuntime()
            .ForAgent("agent1", q => q
                .EnqueueResponse("agent1-response-1")
                .EnqueueResponse("agent1-concurrent")
                .EnqueueResponse("agent1-handoff"))
            .ForAgent("agent2", q => q
                .EnqueueResponse("agent2-response-2")
                .EnqueueResponse("agent2-concurrent")
                .EnqueueResponse("agent2-handoff-final"));

        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new DefaultAgentSessionFactory(runtime, services);
        var host = new WorkflowHost(factory)
            .RegisterAgent("agent1", new AgentDefinition("agent1", []))
            .RegisterAgent("agent2", new AgentDefinition("agent2", []));

        var workflow = new MultiStepAgentWorkflow();
        var finalState = await host.RunAsync(workflow, new PipelineState());

        Assert.Multiple(() =>
        {
            Assert.That(finalState.ResultList, Does.Contain("seq:agent2-response-2"));
            Assert.That(finalState.ResultList, Does.Contain("conc:agent1-concurrent"));
            Assert.That(finalState.ResultList, Does.Contain("conc:agent2-concurrent"));
            Assert.That(finalState.ResultList, Does.Contain("handoff:agent2-handoff-final"));
        });
    }
}
