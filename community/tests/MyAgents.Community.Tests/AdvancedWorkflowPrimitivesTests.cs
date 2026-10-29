using System.Text.Json;
using MyAgents.Core;
using MyAgents.Core.Workflows;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class AdvancedWorkflowPrimitivesTests
{
    private sealed class TestWorkflowSession<TState> : IWorkflowSession<TState>
    {
        private readonly Func<string, IReadOnlyList<AgentMessage>, AgentTurnResult> _handler;

        public TestWorkflowSession(TState initialState, Func<string, IReadOnlyList<AgentMessage>, AgentTurnResult> handler)
        {
            State = initialState;
            _handler = handler;
        }

        public string SessionId => "test-session";
        public TState State { get; set; }

        public ValueTask<AgentTurnResult> CallAgentAsync(
            string agentName,
            IReadOnlyList<AgentMessage> messages,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(_handler(agentName, messages));
        }

        public async IAsyncEnumerable<AgentTurnResult> CallAgentStreamingAsync(
            string agentName,
            IReadOnlyList<AgentMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return _handler(agentName, messages);
            await Task.CompletedTask;
        }

        public HumanInputRequest? GetPendingHumanInput() => null;

        public WorkflowSnapshot CreateSnapshot() =>
            new("test-wf", 1, SessionId, JsonSerializer.Serialize(State));
    }

    [Test]
    public async Task ReflectionSteps_ApprovedOnFirstIteration()
    {
        var session = new TestWorkflowSession<string>("initial", (agent, msgs) =>
        {
            if (agent == "generator")
            {
                return new AgentTurnResult(
                    [new AgentMessage("assistant", "Draft 1 of content.")],
                    new AgentTurnTelemetry(TimeSpan.FromMilliseconds(10)));
            }
            if (agent == "critic")
            {
                return new AgentTurnResult(
                    [new AgentMessage("assistant", "Excellent work. APPROVED.")],
                    new AgentTurnTelemetry(TimeSpan.FromMilliseconds(10)));
            }
            throw new InvalidOperationException($"Unknown agent: {agent}");
        });

        var options = new ReflectionOptions("generator", "critic", MaxIterations: 3);
        var initial = new[] { new AgentMessage("user", "Write an article.") };

        var result = await ReflectionSteps.RunAsync(session, options, initial);

        Assert.That(result.Approved, Is.True);
        Assert.That(result.IterationsCompleted, Is.EqualTo(1));
        Assert.That(result.Iterations.Count, Is.EqualTo(1));
        Assert.That(result.FinalCandidate.Output[0].Content, Is.EqualTo("Draft 1 of content."));
        Assert.That(result.FinalCritique!.Output[0].Content, Contains.Substring("APPROVED"));
    }

    [Test]
    public async Task ReflectionSteps_RefinesUntilApproved_OnSecondIteration()
    {
        var iterationCount = 0;
        var session = new TestWorkflowSession<string>("initial", (agent, msgs) =>
        {
            if (agent == "generator")
            {
                iterationCount++;
                return new AgentTurnResult(
                    [new AgentMessage("assistant", $"Draft {iterationCount} of content.")],
                    new AgentTurnTelemetry(TimeSpan.FromMilliseconds(10)));
            }
            if (agent == "critic")
            {
                var response = iterationCount == 1 ? "Needs more detail." : "Now it is APPROVED.";
                return new AgentTurnResult(
                    [new AgentMessage("assistant", response)],
                    new AgentTurnTelemetry(TimeSpan.FromMilliseconds(10)));
            }
            throw new InvalidOperationException($"Unknown agent: {agent}");
        });

        var options = new ReflectionOptions("generator", "critic", MaxIterations: 3);
        var initial = new[] { new AgentMessage("user", "Write an article.") };

        var result = await ReflectionSteps.RunAsync(session, options, initial);

        Assert.That(result.Approved, Is.True);
        Assert.That(result.IterationsCompleted, Is.EqualTo(2));
        Assert.That(result.Iterations.Count, Is.EqualTo(2));
        Assert.That(result.FinalCandidate.Output[0].Content, Is.EqualTo("Draft 2 of content."));
    }

    [Test]
    public async Task ReflectionSteps_ReachesMaxIterations_WhenNeverApproved()
    {
        var session = new TestWorkflowSession<string>("initial", (agent, msgs) =>
        {
            if (agent == "generator")
            {
                return new AgentTurnResult(
                    [new AgentMessage("assistant", "Draft content.")],
                    new AgentTurnTelemetry(TimeSpan.FromMilliseconds(10)));
            }
            if (agent == "critic")
            {
                return new AgentTurnResult(
                    [new AgentMessage("assistant", "Still contains errors.")],
                    new AgentTurnTelemetry(TimeSpan.FromMilliseconds(10)));
            }
            throw new InvalidOperationException($"Unknown agent: {agent}");
        });

        var options = new ReflectionOptions("generator", "critic", MaxIterations: 2);
        var initial = new[] { new AgentMessage("user", "Write an article.") };

        var result = await ReflectionSteps.RunAsync(session, options, initial);

        Assert.That(result.Approved, Is.False);
        Assert.That(result.IterationsCompleted, Is.EqualTo(2));
        Assert.That(result.Iterations.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task OrchestratorSteps_RunsPlan_AndSynthesizesResult()
    {
        var session = new TestWorkflowSession<int>(0, (agent, msgs) =>
        {
            if (agent == "planner")
            {
                var planJson = """
                    [
                      {
                        "name": "step1",
                        "instruction": "Fetch market data",
                        "workerAgentName": "worker1",
                        "canRunInParallel": false
                      }
                    ]
                    """;
                return new AgentTurnResult(
                    [new AgentMessage("assistant", planJson)],
                    new AgentTurnTelemetry(TimeSpan.FromMilliseconds(10)));
            }
            if (agent == "worker1")
            {
                return new AgentTurnResult(
                    [new AgentMessage("assistant", "Market data: AAPL is up 2%")],
                    new AgentTurnTelemetry(TimeSpan.FromMilliseconds(10)));
            }
            if (agent == "synthesizer")
            {
                return new AgentTurnResult(
                    [new AgentMessage("assistant", "Executive Summary: AAPL is bullish.")],
                    new AgentTurnTelemetry(TimeSpan.FromMilliseconds(10)));
            }
            throw new InvalidOperationException($"Unknown agent: {agent}");
        });

        var options = new OrchestratorOptions(
            PlannerAgentName: "planner",
            WorkerAgentNames: ["worker1"],
            SynthesizerAgentName: "synthesizer");

        var initial = new[] { new AgentMessage("user", "Analyze AAPL") };
        var result = await OrchestratorSteps.RunAsync(session, options, initial);

        Assert.That(result.Plan.Steps.Count, Is.EqualTo(1));
        Assert.That(result.Plan.Steps[0].Name, Is.EqualTo("step1"));
        Assert.That(result.StepResults.Count, Is.EqualTo(1));
        Assert.That(result.StepResults[0].Result.Output[0].Content, Contains.Substring("AAPL is up 2%"));
        Assert.That(result.FinalResult.Output[0].Content, Contains.Substring("Executive Summary"));
    }

    [Test]
    public async Task OrchestratorSteps_RepairsInvalidJson_OnFirstAttempt()
    {
        var plannerCalls = 0;
        var session = new TestWorkflowSession<int>(0, (agent, msgs) =>
        {
            if (agent == "planner")
            {
                plannerCalls++;
                if (plannerCalls == 1)
                {
                    return new AgentTurnResult(
                        [new AgentMessage("assistant", "Sure, here is your plan: not valid json!")],
                        new AgentTurnTelemetry(TimeSpan.FromMilliseconds(10)));
                }

                var repairedJson = """
                    [
                      {
                        "name": "step1",
                        "instruction": "Execute plan",
                        "workerAgentName": "worker1",
                        "canRunInParallel": false
                      }
                    ]
                    """;
                return new AgentTurnResult(
                    [new AgentMessage("assistant", repairedJson)],
                    new AgentTurnTelemetry(TimeSpan.FromMilliseconds(10)));
            }
            if (agent == "worker1")
            {
                return new AgentTurnResult(
                    [new AgentMessage("assistant", "Completed step 1")],
                    new AgentTurnTelemetry(TimeSpan.FromMilliseconds(10)));
            }
            if (agent == "planner")
            {
                return new AgentTurnResult(
                    [new AgentMessage("assistant", "Final synthesized answer.")],
                    new AgentTurnTelemetry(TimeSpan.FromMilliseconds(10)));
            }
            throw new InvalidOperationException($"Unknown agent: {agent}");
        });

        var options = new OrchestratorOptions(
            PlannerAgentName: "planner",
            WorkerAgentNames: ["worker1"],
            MaxRepairAttempts: 2);

        var initial = new[] { new AgentMessage("user", "Run task") };
        var result = await OrchestratorSteps.RunAsync(session, options, initial);

        Assert.That(plannerCalls, Is.GreaterThanOrEqualTo(2));
        Assert.That(result.Plan.Steps.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task OrchestratorSteps_ExtractsPlan_FromMarkdownFencedJsonAndCommentary()
    {
        var session = new TestWorkflowSession<int>(0, (agent, msgs) =>
        {
            if (agent == "planner")
            {
                var response = """
                    Here is the planned sequence of operations for the workers:
                    ```json
                    [
                      {
                        "name": "step1",
                        "instruction": "Fetch market data",
                        "workerAgentName": "worker1",
                        "canRunInParallel": false
                      }
                    ]
                    ```
                    Please proceed accordingly.
                    """;
                return new AgentTurnResult(
                    [new AgentMessage("assistant", response)],
                    new AgentTurnTelemetry(TimeSpan.FromMilliseconds(10)));
            }
            if (agent == "worker1")
            {
                return new AgentTurnResult(
                    [new AgentMessage("assistant", "Market data: AAPL is up 2%")],
                    new AgentTurnTelemetry(TimeSpan.FromMilliseconds(10)));
            }
            if (agent == "synthesizer")
            {
                return new AgentTurnResult(
                    [new AgentMessage("assistant", "Executive Summary: AAPL is bullish.")],
                    new AgentTurnTelemetry(TimeSpan.FromMilliseconds(10)));
            }
            throw new InvalidOperationException($"Unknown agent: {agent}");
        });

        var options = new OrchestratorOptions(
            PlannerAgentName: "planner",
            WorkerAgentNames: ["worker1"],
            SynthesizerAgentName: "synthesizer");

        var initial = new[] { new AgentMessage("user", "Analyze AAPL") };
        var result = await OrchestratorSteps.RunAsync(session, options, initial);

        Assert.That(result.Plan.Steps.Count, Is.EqualTo(1));
        Assert.That(result.Plan.Steps[0].Name, Is.EqualTo("step1"));
        Assert.That(result.StepResults.Count, Is.EqualTo(1));
        Assert.That(result.StepResults[0].Result.Output[0].Content, Contains.Substring("AAPL is up 2%"));
        Assert.That(result.FinalResult.Output[0].Content, Contains.Substring("Executive Summary"));
    }

    [Test]
    public async Task GoalPrimitives_ManageStatuses_AndSelectHighestPriorityReady()
    {
        var goals = new GoalCollection();

        var g1 = goals.Add(new GoalDefinition("Goal 1", "Objective 1", priority: GoalPriority.Normal), goalId: "g1", status: GoalStatus.Ready);
        var g2 = goals.Add(new GoalDefinition("Goal 2", "Objective 2", priority: GoalPriority.Critical), goalId: "g2", status: GoalStatus.Ready);
        var g3 = goals.Add(new GoalDefinition("Goal 3", "Objective 3", priority: GoalPriority.Critical), goalId: "g3", status: GoalStatus.Proposed);

        Assert.That(goals.Goals.Count, Is.EqualTo(3));

        var next = await GoalSteps.SelectNextAsync(goals);
        Assert.That(next, Is.Not.Null);
        Assert.That(next!.GoalId, Is.EqualTo("g2"));

        GoalSteps.MarkCompleted(goals, "g2");
        Assert.That(goals.Get("g2")!.Status, Is.EqualTo(GoalStatus.Completed));

        var nextAfterComplete = await GoalSteps.SelectNextAsync(goals);
        Assert.That(nextAfterComplete, Is.Not.Null);
        Assert.That(nextAfterComplete!.GoalId, Is.EqualTo("g1"));

        GoalSteps.MarkBlocked(goals, "g1", reason: "Blocked on external dependency");
        Assert.That(goals.Get("g1")!.Status, Is.EqualTo(GoalStatus.Blocked));

        var nextAfterBlocked = await GoalSteps.SelectNextAsync(goals);
        Assert.That(nextAfterBlocked, Is.Null);
    }

    [Test]
    public async Task AdaptationSteps_GeneratesHintsFromOutcomesAndFeedback()
    {
        var outcomes = new List<OutcomeRecord>();
        AdaptationSteps.RecordOutcome(
            outcomes,
            name: "fetch-api",
            status: OutcomeStatus.Failed,
            summary: "HTTP 504 Gateway Timeout",
            goalId: "goal-api");

        var feedback = new List<FeedbackRecord>();
        AdaptationSteps.RecordFeedback(
            feedback,
            kind: FeedbackKind.Human,
            summary: "The response was inaccurate",
            score: 0.2,
            goalId: "goal-api");

        var adaptationResult = await AdaptationSteps.EvaluateAsync(outcomes, feedback);

        Assert.That(adaptationResult.Hints.Count, Is.GreaterThan(0));
        Assert.That(adaptationResult.Hints.Any(h => h.Kind == AdaptationHintKind.Prompt), Is.True);
        Assert.That(adaptationResult.Hints.Any(h => h.Kind == AdaptationHintKind.Goal), Is.True);

        var promptMessages = AdaptationSteps.BuildAdaptationMessages(adaptationResult.Hints);
        Assert.That(promptMessages.Count, Is.EqualTo(1));
        Assert.That(promptMessages[0].Content, Contains.Substring("Adaptation hints:"));
    }

    [Test]
    public async Task RoutingSteps_SelectsCandidate_WithLowerLatencyAndHeadroom()
    {
        var candidateFast = new RoutingCandidate(
            candidateId: "model-fast",
            target: "fast-endpoint",
            estimatedLatency: TimeSpan.FromMilliseconds(50),
            estimatedCost: 0.001,
            maxInputTokens: 8192,
            capabilities: ["chat", "code"]);

        var candidateSlow = new RoutingCandidate(
            candidateId: "model-slow",
            target: "slow-endpoint",
            estimatedLatency: TimeSpan.FromMilliseconds(800),
            estimatedCost: 0.005,
            maxInputTokens: 8192,
            capabilities: ["chat", "code"]);

        var context = new RoutingContext(
            Candidates: [candidateSlow, candidateFast],
            EstimatedInputTokens: 500,
            RequiredCapabilities: [new RoutingCapabilityRequirement("code")]);

        var selected = await RoutingSteps.SelectAsync(context);

        Assert.That(selected, Is.Not.Null);
        Assert.That(selected!.Candidate.CandidateId, Is.EqualTo("model-fast"));
    }
}
