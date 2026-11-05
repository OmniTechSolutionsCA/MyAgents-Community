# MyAgents.Runtime.Workers.Sqlite

SQLite persistence for `IAgentRunBackend`. This optional adapter stores run state in a local
database file and uses SQLite write transactions to serialize claims and lifecycle updates.

```csharp
services.AddSingleton<IAgentRunBackend>(
    _ => new SqliteAgentRunBackend(
        "agent-runs.db",
        timeProvider: null,
        options: new AgentRunBackendOptions
        {
            MaxOutstandingRunsPerScope = 100,
            MaxLeasedRunsPerScope = 4,
        }));
services.AddSingleton<IAgentRunHandler, MyRunHandler>();
services.AddMyAgentsRunWorker();
```

Use a durable local disk and back up the database according to your retention needs. SQLite is a
single-machine persistence option; this package does not provide a distributed database or
multi-host scale guarantee. Use operation-level idempotency for external side effects because run
delivery remains at-least-once.

Per-scope run limits are recorded in the database on first use. Every backend instance opening the
same file must use the same limit values; startup fails when they differ.

See the `community/samples/DurableAgentRuns` project for a runnable worker example.
