# Durable Agent Runs

`MyAgents.Runtime.Workers` and `MyAgents.Runtime.Workers.Sqlite` enable durable, restart-recoverable agent workloads using standard .NET Generic Host worker patterns.

## Architecture

```mermaid
flowchart TD
    Client[Submit Request] --> Backend[IAgentRunBackend (Sqlite)]
    Backend --> Queue[(Persisted Runs Table)]
    WorkerService[AgentRunWorkerService] --> Lease[Acquire Lease]
    Queue --> Lease
    Lease --> Handler[IAgentRunHandler]
    Handler --> Result[AgentRunHandlerResult]
    Result --> Complete[Mark Completed / Failed]
    Complete --> Queue
```

## Setup

Configure the durable worker with SQLite persistence:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyAgents.Core.Execution;
using MyAgents.Runtime.Workers;
using MyAgents.Runtime.Workers.Sqlite;

var builder = Host.CreateApplicationBuilder();

// 1. Register the SQLite run backend
builder.Services.AddSingleton<IAgentRunBackend>(_ => 
    new SqliteAgentRunBackend("agent-runs.db"));

// 2. Register your custom run handler
builder.Services.AddSingleton<IAgentRunHandler, MyDurableAgentHandler>();

// 3. Register the background worker service
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

## Submitting a Run

Runs can be submitted from any web API or CLI client:

```csharp
var backend = host.Services.GetRequiredService<IAgentRunBackend>();

var request = new AgentRunRequest(
    HandlerId: "research-assistant",
    InputJson: """{"topic": "Quantum Computing", "depth": "advanced"}""",
    IdempotencyKey: "run-query-101",
    ScopeKey: "user-42"
);

var submission = await backend.SubmitAsync(request);
Console.WriteLine($"Run {submission.RunId} submitted. Deduplicated: {submission.WasDeduplicated}");
```

If the hosting process crashes or is restarted during execution, the active lease expires and another worker automatically reclaims the run and resumes execution.
