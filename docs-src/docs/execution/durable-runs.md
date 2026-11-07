# Durable Agent Runs

`MyAgents.Runtime.Workers` and `MyAgents.Runtime.Workers.Sqlite` provide a durable, asynchronous task execution engine for long-running agent workloads.

## Why Durable Runs?

In production, agents often execute complex, multi-minute tasks such as crawling web pages, ingesting datasets, or conducting multi-step research. Running these inside a standard HTTP request is brittle:
- Server deployments or container restarts kill in-flight requests.
- Transient network drops cause duplicate agent executions.
- Load spikes can overwhelm LLM provider rate limits.

Durable Runs decouple submission from execution using a **lease-based background worker pool**.

---

## Architecture

```mermaid
flowchart TD
    subgraph Submission
        Client[Web API / CLI] -->|SubmitAsync| Backend[IAgentRunBackend (Sqlite)]
        Backend -->|Write Pending Record| Table[(Persistent Runs Store)]
    end

    subgraph Worker Pool
        WorkerService[AgentRunWorkerService] -->|TryAcquireLeaseAsync| Table
        Table -->|Leased to Worker 1| WorkerService
        WorkerService -->|Heartbeat Renewal| Table
        WorkerService -->|Execute| Handler[IAgentRunHandler]
        Handler -->|Output Result| Result[AgentRunHandlerResult]
        Result -->|Mark Completed / Failed| Table
    end
```

---

## 1. Configuring the Durable Worker Host

Configure your background service using standard .NET Generic Host:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyAgents.Core.Execution;
using MyAgents.Runtime.Workers;
using MyAgents.Runtime.Workers.Sqlite;

var builder = Host.CreateApplicationBuilder();

// 1. Register SQLite persistence backend
builder.Services.AddSingleton<IAgentRunBackend>(_ => 
    new SqliteAgentRunBackend("agent-runs.db"));

// 2. Register your custom run handler
builder.Services.AddSingleton<IAgentRunHandler, DataProcessingAgentHandler>();

// 3. Register the durable worker service
builder.Services.AddMyAgentsRunWorker(options =>
{
    options.IdlePollInterval = TimeSpan.FromMilliseconds(250);
    options.LeaseDuration = TimeSpan.FromSeconds(30);
    options.LeaseRenewalInterval = TimeSpan.FromSeconds(10);
    options.BaseRetryDelay = TimeSpan.FromSeconds(2);
    options.MaximumRetryDelay = TimeSpan.FromMinutes(5);
});

using var host = builder.Build();
await host.RunAsync();
```

---

## 2. Implementing `IAgentRunHandler`

A run handler executes the actual business logic for an agent run:

```csharp
public sealed class DataProcessingAgentHandler : IAgentRunHandler
{
    private readonly IAgentSessionFactory _sessionFactory;

    public DataProcessingAgentHandler(IAgentSessionFactory sessionFactory)
    {
        _sessionFactory = sessionFactory;
    }

    public async ValueTask<AgentRunHandlerResult> HandleAsync(
        AgentRunContext context,
        CancellationToken cancellationToken)
    {
        // Create an isolated session for this durable run
        await using var session = _sessionFactory.CreateSession(context.RunId, MyAgentDefinitions.Default);

        var userMessage = AgentMessage.FromText("user", context.InputJson);
        var result = await session.InvokeAsync([userMessage], cancellationToken);

        return AgentRunHandlerResult.Completed(result.Output.Last().Content);
    }
}
```

---

## 3. Submitting Runs with Idempotency

Submissions are resilient to duplicate calls:

```csharp
var backend = serviceProvider.GetRequiredService<IAgentRunBackend>();

var request = new AgentRunRequest(
    HandlerId: "data-processing",
    InputJson: """{"dataset": "sales-q3", "action": "audit"}""",
    IdempotencyKey: "sales-q3-audit-2026",
    ScopeKey: "tenant-acme"
);

var submission = await backend.SubmitAsync(request, cancellationToken);

if (submission.WasDeduplicated)
{
    Console.WriteLine($"Duplicate request detected. Returning existing RunId: {submission.RunId}");
}
else
{
    Console.WriteLine($"New Run enqueued: {submission.RunId}");
}
```

---

## Crash Recovery & Lease Management

1. **Lease Acquisition**: A worker acquires a lease on a run with an expiration timestamp (`LeaseExpiresAt`).
2. **Heartbeats**: While processing, the worker automatically renews its lease every `LeaseRenewalInterval`.
3. **Automatic Recovery**: If the host process is killed or panics, the lease expires. Another worker claims the expired lease, increments the attempt count, and retries or resumes execution.
