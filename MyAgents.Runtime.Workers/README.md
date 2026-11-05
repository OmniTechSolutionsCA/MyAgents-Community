# MyAgents.Runtime.Workers

Optional hosted execution for applications that provide an `IAgentRunBackend` and one or more
`IAgentRunHandler` implementations. The worker package has no queue-vendor dependency; a durable
backend adapter must provide atomic claims, lease fencing, persistence, and coordination across
worker processes.

```csharp
services.AddSingleton<IAgentRunBackend, MyDurableRunBackend>();
services.AddSingleton<IAgentRunHandler, MyWorkflowHandler>();
services.AddMyAgentsRunWorker(options =>
{
    options.MaxConcurrentRuns = 4;
    options.LeaseDuration = TimeSpan.FromSeconds(45);
    options.LeaseRenewalInterval = TimeSpan.FromSeconds(10);
});
```

`WorkflowAgentRunHandler<TState>` adapts a typed `WorkflowHost` workflow and requires explicit
`JsonTypeInfo<T>` metadata. It saves snapshots at human-input and tool-approval pauses. A workflow
that needs recovery between those pauses must persist its own checkpoints through
`AgentRunContext.SaveCheckpointAsync`.

`MaxConcurrentRuns` bounds active handlers per service instance and defaults to one. Backend
implementations can also cap outstanding and leased runs per `ScopeKey`; these limits count runs,
not tokens or cost. During host shutdown, active work is cancelled and its lease is left to expire
so a durable backend can recover the run.

`AgentRunBatchSubmitter` streams batch cases into the same run path. `AgentRunBatchReporter`
summarizes terminal and pending outcomes and adds payload-free trace event counts when an
`IRunTraceSink` is registered. `AgentRunBatchController` propagates cancellation to the batch's
queued and leased runs. The configured backend must implement the batch catalog and controller
contracts for these helpers to work.

Delivery is at-least-once. The worker passes a stable operation ID helper to handlers, but external
systems must enforce idempotency for side effects. `InMemoryAgentRunBackend` is in
`MyAgents.Testing` for local use and deterministic tests; it is process-local and not durable.
