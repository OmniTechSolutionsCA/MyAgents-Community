# Checkpointed Workflows & Human-in-the-Loop

`MyAgents.Core.Workflows` provides `IWorkflow<TState>` and `WorkflowHost` for imperative, multi-agent C# workflows that can pause for human input or tool approvals, serialize their state to a `WorkflowSnapshot`, and resume later—even across process restarts.

## 1. Defining a Stateful `IWorkflow<TState>`

Implement `IWorkflow<TState>` and use `WorkflowSteps` (`RequestHumanInputAsync`, `RequestToolApprovalAsync`, `RunSequentialAsync`, `RunConcurrentAsync`, `RunHandoffAsync`, `RunStreamingBridgeAsync`):

```csharp
using MyAgents.Core;
using MyAgents.Core.Workflows;

public sealed record ReleaseWorkflowState(
    string Version,
    string ReleaseNotes = "",
    bool ApprovedByReleaseManager = false,
    string ManagerComment = "");

public sealed class ReleasePipelineWorkflow : IWorkflow<ReleaseWorkflowState>
{
    public string Id => "release-pipeline";
    public int StateVersion => 1;

    public async ValueTask RunAsync(
        IWorkflowSession<ReleaseWorkflowState> session,
        CancellationToken cancellationToken = default)
    {
        // Step 1: Generate release notes if not yet drafted
        if (string.IsNullOrEmpty(session.State.ReleaseNotes))
        {
            var turn = await session.CallAgentAsync(
                "release-writer",
                [AgentMessage.FromText("user", $"Draft release notes for {session.State.Version}")],
                cancellationToken);

            session.State = session.State with { ReleaseNotes = turn.Output[0].Content };

            // Pause workflow and request human confirmation (throws HumanInputRequiredException with snapshot)
            await WorkflowSteps.RequestHumanInputAsync(
                session,
                stepId: "manager-signoff",
                prompt: $"Approve release {session.State.Version}?",
                cancellationToken: cancellationToken);
        }

        // Step 2: Process human reply upon resumption
        HumanInputRequest? humanInput = session.GetPendingHumanInput();
        if (humanInput is not null)
        {
            bool approved = string.Equals(humanInput.Reply?.Trim(), "approve", StringComparison.OrdinalIgnoreCase);
            session.State = session.State with
            {
                ApprovedByReleaseManager = approved,
                ManagerComment = humanInput.Reply ?? string.Empty
            };
        }
    }
}
```

---

## 2. Running, Checkpointing & Resuming with `WorkflowHost`

`WorkflowHost` executes `IWorkflow<TState>` instances and integrates with `IWorkflowCheckpointer` implementations:
- **`SqliteCheckpointer`**: Persists snapshots in a local SQLite database table.
- **`FileSystemCheckpointer`**: Persists snapshots as JSON files on disk.
- **`DistributedCacheCheckpointer`**: Persists snapshots in any `IDistributedCache` (Redis, SQL Server, etc.).

```csharp
using MyAgents.Core.Workflows;

var host = new WorkflowHost(sessionFactory)
    .RegisterAgent("release-writer", writerDefinition);

using var checkpointer = new SqliteCheckpointer("workflows.db", tableName: "workflow_snapshots");
var workflow = new ReleasePipelineWorkflow();

WorkflowSnapshot? pausedSnapshot = null;

try
{
    await host.RunAsync(workflow, new ReleaseWorkflowState(Version: "v1.0.0"), cancellationToken);
}
catch (HumanInputRequiredException ex)
{
    // Persist the snapshot while waiting for the human operator
    pausedSnapshot = ex.Snapshot;
    await checkpointer.SaveAsync(pausedSnapshot, cancellationToken);
    Console.WriteLine($"Workflow paused at step '{pausedSnapshot.PendingHumanInput?.StepId}': {pausedSnapshot.PendingHumanInput?.Prompt}");
}

// Later (e.g. when webhook arrives), load the snapshot and resume with the human's reply:
WorkflowSnapshot? loaded = await checkpointer.LoadAsync(pausedSnapshot!.SessionId, cancellationToken);

ReleaseWorkflowState finalState = await host.ResumeWithInputAsync(
    workflow: workflow,
    snapshot: loaded!,
    humanReply: "approve",
    cancellationToken: cancellationToken);

Console.WriteLine($"Approved: {finalState.ApprovedByReleaseManager}");
```

---

## 3. Built-in Multi-Agent `WorkflowSteps`

Inside `IWorkflow<TState>.RunAsync`, `WorkflowSteps` provides composable multi-agent execution helpers:

- **`WorkflowSteps.RunSequentialAsync(session, agentNames, initialMessages)`**: Chains output from each agent as input to the next agent in sequence.
- **`WorkflowSteps.RunConcurrentAsync(session, agentNames, messages)`**: Invokes multiple agents in parallel on the same input and returns all `AgentTurnResult` responses.
- **`WorkflowSteps.RunHandoffAsync(session, firstAgentName, initialMessages, nextAgentSelector, termination, maxHops)`**: Dynamically transfers control between agents based on a selector function until termination or `maxHops`.
- **`WorkflowSteps.RunStreamingBridgeAsync(...)`**: Streams chunks from a source agent while compacting and forwarding the completed message to downstream agents.

---

## 4. Hosting Checkpointed Workflows in Durable Workers (`WorkflowAgentRunHandler<TState>`)

`MyAgents.Runtime.Workers` provides `WorkflowAgentRunHandler<TState>` to bridge `IWorkflow<TState>` directly into the durable worker engine (`AgentRunWorkerService`). When a workflow calls `RequestHumanInputAsync` or `RequestToolApprovalAsync`, `WorkflowAgentRunHandler<TState>` automatically transitions the durable run to `AgentRunStatus.WaitingForInput` and persists the `WorkflowSnapshot` in the run's `CheckpointJson`!
