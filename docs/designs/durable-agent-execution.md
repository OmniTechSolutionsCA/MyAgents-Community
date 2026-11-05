# Durable Agent Execution Primitives

**Status:** Local durable execution and admission/batch primitives implemented; multi-host backend and deployment validation remain pending
**Date:** 2026-11-03
**Scope:** Portable primitives for queue-backed, restartable agent work in MyAgents Community.

## Summary

Provide an optional execution layer that lets applications submit agent work, have workers claim it, recover it after worker loss, and inspect its lifecycle. It reuses MyAgents workflows, checkpoints, model clients, policies, and trace events while leaving queue vendors, hosting environments, and deployment operations to adapters and adopters.

This proposal is an enabling framework feature. It does not claim that MAC itself is a hosted distributed platform or provide a token-throughput guarantee.

## Implementation status

The current branch contains:

- Provider-neutral `IAgentRunBackend`, run lifecycle records, lease fencing, handler, and context contracts in `MyAgents.Core`.
- `InMemoryAgentRunBackend` in `MyAgents.Testing`, including scoped idempotency, retries, cancellation, deadlines, input waits, and stale-lease rejection.
- The optional `MyAgents.Runtime.Workers` hosted worker and a typed `WorkflowAgentRunHandler<TState>` adapter.
- The optional `MyAgents.Runtime.Workers.Sqlite` adapter and a restart/recovery sample.
- Configurable worker concurrency, atomic per-scope outstanding/leased run caps, and batch/case submission, cancellation, and outcome summaries.
- Deterministic backend tests and public API approval baselines.

The SQLite adapter persists across process restarts and serializes claims through local database transactions. It is a single-machine option; it does not provide a distributed database, multi-host failover, or throughput evidence. Worker concurrency is bounded per service instance by `MaxConcurrentRuns`, defaulting to one. Backend options can cap nonterminal and simultaneously leased runs per scope; these are run-count limits, not token or cost budgets. All SQLite backend instances sharing one database must use the same configured limits. `WorkflowAgentRunHandler<TState>` saves `WorkflowHost` snapshots at human-input and tool-approval pauses. It does not automatically checkpoint each workflow step; handlers that need intermediate recovery must save handler-defined checkpoints through `AgentRunContext`. Batch summaries include lifecycle outcomes and, when a trace sink is registered, payload-free event counts. Judge scoring and synthetic-data generation remain future work.

The recovery sample was exercised end-to-end: attempt 1 was interrupted, the process restarted with the same database, the same run was returned by idempotency, and attempt 2 reclaimed the expired lease and succeeded.

## Problem and current baseline

MAC already has useful pieces:

- `WorkflowHost` can start and resume workflows.
- `IWorkflowCheckpointer` has file, SQLite, and `IDistributedCache` implementations.
- Agent middleware includes transient retries and circuit breakers.
- `MyAgents.Runtime.Chat` executes turns and tool calls in process through `IChatClient`.
- `MyAgents.Core.Execution` defines run trace records and sinks.
- `MyAgents.Core` has a turn-level dead-letter sink contract.

These pieces do not provide a durable work queue, atomic claim/lease lifecycle, worker host, or a consistent recovery policy for a run that outlives a process. A workflow checkpoint preserves workflow state; it does not schedule work or prove that external tool side effects will not be repeated.

The existing `IDeadLetterQueue` is for handling an agent-turn exception through `IFallbackPolicy`. Run-level retries and terminal job state have different lifecycle semantics and should not silently reuse that abstraction.

## Goals

1. Let an application submit one logical run and receive a stable run identifier.
2. Let multiple workers safely claim runs with expiring leases and reject updates from stale workers.
3. Persist lifecycle state so queued work can be recovered after process or worker failure.
4. Resume workflow state from the existing checkpoint contract where possible.
5. Make cancellation, deadlines, attempts, retry scheduling, and terminal failure explicit.
6. Correlate run lifecycle and execution traces using the existing `RunId` trace field.
7. Keep provider and broker SDK dependencies out of `MyAgents.Core`; make hosting and adapters optional.

## Non-goals

- A hosted SaaS control plane, web API, or deployment product.
- A model-serving API or provider-specific model client. MAC already accepts `IChatClient`; applications supply providers.
- Exactly-once execution of arbitrary external side effects. The supported delivery model is at-least-once.
- A distributed transaction spanning a queue, checkpoint store, and application database.
- A training platform or a built-in synthetic-data model. Batch evaluation and data generation can build on the run layer later.
- A claim that a particular queue, database, or worker configuration handles a fixed token volume without deployment-specific evidence.

## Proposed architecture

```text
Application or dataset feeder
             |
             v
       Run submission API
             |
             v
   Durable run backend <----> Worker host(s)
             |                       |
             |                       v
             |            Run handler / WorkflowHost
             |                 |             |
             |                 v             v
             |           Checkpointer    IChatClient + tools
             |                       \
             +---- lifecycle events ----> IRunTraceSink
```

### Contract boundary

The first implementation provides one provider-neutral `IAgentRunBackend` contract for submission, idempotency, lifecycle queries, claim/lease transitions, and completion/retry transitions. Broker and database adapters may split that work internally, but callers should not have to coordinate a queue write with a separate status-store write. Adapter implementations must document how they handle atomicity or an outbox when multiple stores are involved.

The contract should expose operations conceptually equivalent to:

- `SubmitAsync(request)` — enqueue a run and return its stable ID; repeated submissions with the same scoped idempotency key resolve to the same logical run.
- `ClaimAsync(workerId, leaseDuration)` — atomically claim the next eligible run and return a lease with a fencing token and attempt number.
- `RenewLeaseAsync(lease, leaseDuration)` — extend a live lease; report if the lease is stale or has been superseded.
- `CompleteAsync(lease, outputJson)` — atomically record successful completion for the current lease.
- `RetryAsync(lease, failure, retryDelay)` — schedule another attempt or transition to terminal failure when policy is exhausted.
- `CancelAsync(runId)` and `GetAsync(runId)` — request cancellation and inspect the durable lifecycle.

These are design-level operations, not approved public method signatures. Before implementation, choose whether the public surface is one `IAgentRunBackend` or a small pair of dispatcher/backend interfaces. Avoid a public API that requires callers to implement distributed transaction logic themselves.

### Run request and lifecycle

A submitted run needs, at minimum:

- a stable `RunId` generated or accepted by the backend;
- a registered handler/workflow identifier and version;
- a serialized, versioned input payload;
- an optional idempotency key, deadline, and retry policy;
- optional opaque scope/correlation metadata for adopter-defined partitioning.

The durable status model should distinguish at least `Queued`, `Leased`, `WaitingForInput`, `RetryScheduled`, `Succeeded`, `Failed`, and `Cancelled`. A dead-letter or exhausted state may be represented separately if it carries an operator action. Human-input and approval pauses should not consume retry attempts.

Avoid hard-coding a specific tenant model in Core. The backend can use an opaque scope key, while admission-control policies later define how adopters map it to tenant or workload limits.

### Worker and recovery semantics

An optional worker host should:

1. Claim a run and start lease heartbeats.
2. Resolve a registered handler; do not deserialize arbitrary CLR type names from an untrusted queue payload.
3. Execute with a stable run context containing `RunId`, attempt, deadline, cancellation, and the current lease/fencing token.
4. Persist a workflow checkpoint before acknowledging successful completion.
5. Renew the lease during long operations, stop work when cancellation/deadline/lease loss is observed, then complete or schedule retry.

Lease fencing prevents a worker whose lease expired from acknowledging or overwriting newer run state. It cannot undo a side effect already performed by that stale worker. Tool implementations that perform external writes need an idempotency key derived from the run and operation, or their own deduplication. MAC must document this clearly and avoid an exactly-once guarantee.

Run-level retries are separate from the short provider-call retries in `ResilienceMiddleware`. Policies must prevent retry multiplication, respect the run deadline, and distinguish transient failures, permanent failures, cancellation, and human waits.

### Relationship to current contracts

- Reuse `IWorkflowCheckpointer` for workflow snapshots; associate a run with its workflow session and schema version. Do not make a second checkpoint format in the first slice.
- Treat the durable run backend as the source of truth for run status. `IRunTraceSink` remains an observability stream and may be unavailable without corrupting lifecycle state.
- Add run/attempt/worker correlation to execution events where needed, while keeping sensitive input/output payload capture opt-in and redacted by default.
- Keep `IDeadLetterQueue` for turn-level fallback behavior. A queue adapter may offer a bridge, but run exhaustion should have a run-specific record and retry history.

## Package placement

- **Core:** minimal run request, status, lease, and backend contracts only if they remain provider-neutral and do not require hosted-service or broker packages.
- **Optional worker package:** `MyAgents.Runtime.Workers` provides a .NET Generic Host worker that consumes the backend and invokes registered handlers.
- **Adapters:** broker/database implementations live in separate optional packages. Select one only after choosing the first adopter scenario; do not add vendor SDKs to Core.
- **Testing:** deterministic in-memory backend and controllable clock/failure hooks belong in `MyAgents.Testing`.

`MyAgents.Runtime.Chat` remains useful for in-process applications and does not depend on a queue or hosted service.

## Delivery guarantees and invariants

The first version should state these invariants explicitly:

1. Submission is idempotent within a documented scope when an idempotency key is supplied.
2. Only one valid lease generation can mutate a run at a time.
3. Expired leases make work eligible for recovery; they do not prove the previous worker stopped.
4. A run is acknowledged only after its handler has produced a terminal result and required checkpoint writes succeeded.
5. Execution is at-least-once across worker failure. External side effects require operation-level idempotency or deduplication.
6. Trace delivery failure does not change run status or cause a completed run to execute again.
7. Retry exhaustion, explicit cancellation, deadline expiry, and human wait each have distinct terminal or paused states.

## Incremental delivery

### Slice 1 — Contracts and deterministic worker semantics (implemented)

- Define run request/status/lease types and atomic backend operations.
- Add an in-memory backend with fake time for deterministic queue, lease-expiry, retry, cancellation, and stale-worker tests.
- Add a general handler interface and an optional hosted worker that invokes registered handlers.
- Add a typed `WorkflowHost` adapter for workflow input and human-input/tool-approval pauses.
- Treat `AgentRunRequest.InputJson` as a versioned application payload; checkpoint payload versioning belongs to the handler.

This slice proves the public lifecycle semantics without choosing a cloud broker.

### Slice 2 — One durable adapter and recovery sample (implemented for local SQLite)

- Add the `MyAgents.Runtime.Workers.Sqlite` package with transactional claim, lease, idempotency, and lifecycle persistence.
- Add a sample that can be interrupted and restarted against the same database to demonstrate lease recovery.
- Verify persisted run submission, lease fencing, concurrent claims by two backend instances, and waiting/resume state.
- Document local retries versus queue retries, idempotent side effects, retention, and operational ownership.

SQLite is the local reference adapter. A multi-host backend remains a future adapter decision driven by the first deployment target.

### Slice 3 — Admission control and batch execution (initial primitives implemented)

- Bound active work per worker service with `AgentRunWorkerOptions.MaxConcurrentRuns`.
- Enforce optional per-scope outstanding and leased run-count limits in the backend. SQLite applies both limits inside its transaction boundary.
- Stream batch cases into the run path with batch/case IDs; query grouped outcomes, summarize status counts, and cancel a batch.
- Include payload-free trace event counts in batch summaries when a trace sink is available.
- Add judge scoring and synthetic-data handlers in a later increment.

## Acceptance criteria for the first usable release

- Duplicate submission with the same scoped idempotency key does not create a second logical run.
- A second worker cannot claim a currently leased run; after expiry, one worker can reclaim it with a higher fencing token.
- A stale worker cannot renew, complete, or overwrite a reclaimed run.
- A worker restart resumes from the most recent valid workflow checkpoint and increments the attempt count.
- Retry delay, max attempts, run deadline, cancellation, and human wait produce distinct, queryable states.
- A trace sink failure does not corrupt run lifecycle state.
- A sample shows a run flowing from submission through a worker to `WorkflowHost` and a provider-neutral `IChatClient`, with no provider SDK in Core.
- Documentation explicitly states at-least-once semantics and the adopter's responsibility for idempotent external side effects.

## Open decisions

1. Which first adopter scenario should drive the backend: long-running interactive workflows, batch evaluation, or synthetic-data jobs?
2. Which multi-host backend should follow the SQLite reference adapter? The choice should come from the first adopter's deployment target and required enqueue/claim/status guarantees.
3. The first slice exposes a general handler contract and a `WorkflowHost` adapter; adopter feedback can refine the adapter before it is treated as stable.
4. What should happen to run results and payloads after completion: backend-defined retention, caller-owned artifact storage, or a later result-store abstraction?
5. How should handlers receive operation idempotency keys without expanding the existing `ITool` contract prematurely?
6. What payload and trace redaction defaults are required for production use?

## Recommendation

The run lifecycle contract, local durable adapter, bounded worker, per-scope run-count limits, and batch outcome primitives are now in place. The next infrastructure decision is a multi-host backend tied to an adopter deployment; trace-backed evaluation and synthetic-data workflows can build on the batch path after that choice.
