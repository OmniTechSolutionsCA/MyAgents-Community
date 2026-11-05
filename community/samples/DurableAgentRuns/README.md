# Durable agent run sample

This sample submits a long-running handler to `SqliteAgentRunBackend` and executes it through the
optional hosted worker. The first attempt takes 20 seconds so it can be interrupted to demonstrate
lease recovery.

```powershell
dotnet run --project community/samples/DurableAgentRuns -- .\agent-runs.db
```

Start the sample, interrupt it while the run is `Leased`, and start it again with the same database
path. The stable scoped idempotency key returns the existing run. After the five-second lease
expires, the worker reclaims it with a higher fencing token and completes the run. The handler may
execute more than once; external side effects must use idempotency keys of their own.

The sample uses local SQLite persistence. It demonstrates process restart and lease recovery on one
machine, not distributed database failover or multi-host throughput.
