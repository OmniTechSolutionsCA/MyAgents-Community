# Circuit Breakers, Tool Observers & OpenTelemetry

To protect downstream dependencies from cascading failures and give operators full visibility into agent behavior, MyAgents Community provides circuit breaker middleware, tool call observers, and first-class OpenTelemetry tracing and metrics.

## 1. Circuit Breaker Middleware (`CircuitBreakerMiddleware`)

When an LLM provider or downstream dependency experiences transient failures (such as HTTP 429/503 errors or timeouts), `CircuitBreakerMiddleware` tracks consecutive transient failures via `ITransientErrorClassifier` and trips open (`CircuitState.Open`), fast-failing with `CircuitBreakerOpenException` until `OpenDuration` elapses:

```csharp
using MyAgents.Core;

var circuitBreaker = new CircuitBreakerMiddleware(
    failureThreshold: 3,
    openDuration: TimeSpan.FromSeconds(30),
    classifier: new DefaultTransientErrorClassifier());

// Inspect state (Closed, Open, HalfOpen) or manually reset
Console.WriteLine($"Circuit state: {circuitBreaker.State}");
circuitBreaker.Reset();
```

Register `CircuitBreakerMiddleware` in an agent's pipeline using a custom feature or `assembly.Pipeline.Use(circuitBreaker)`.

---

## 2. Tool Call Observers (`IToolCallObserver`)

`IToolCallObserver` lets you log, audit, or time every tool execution without modifying the tool implementation itself:

```csharp
using Microsoft.Extensions.Logging;
using MyAgents.Agents;
using MyAgents.Core;
using MyAgents.Core.Tools;

public sealed class SecurityAuditToolObserver : IToolCallObserver
{
    private readonly ILogger _logger;

    public SecurityAuditToolObserver(ILogger logger)
    {
        _logger = logger;
    }

    public ValueTask OnToolCallingAsync(
        ToolCallObservation call,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Session {SessionId} invoking tool {ToolName} (CallId: {CallId}) with args: {Args}",
            call.SessionId, call.ToolName, call.CallId, call.Arguments);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnToolCalledAsync(
        ToolResultObservation result,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Tool {ToolName} finished in {ElapsedMs:F1}ms (Failed: {HasError})",
            result.Call.ToolName,
            result.Duration.TotalMilliseconds,
            result.Exception is not null);
        return ValueTask.CompletedTask;
    }
}
```

Attach one or more observers to an `AgentDefinition` via `ToolObserverFeature`:

```csharp
var definition = new AgentDefinition("audited-agent", [
    new ToolObserverFeature(new SecurityAuditToolObserver(logger)),
    new ToolsFeature(myTool)
]);
```

---

## 3. OpenTelemetry Tracing & Metrics (`MyAgentsOtelExtensions`)

`MyAgents.Core` (`MyAgentsTelemetry`) and `MyAgents.Agents` (`TelemetryFeature`, `MyAgentsOtelExtensions`) integrate directly with `System.Diagnostics.ActivitySource`, `System.Diagnostics.Metrics.Meter`, and `.NET Aspire`:

### Enabling Telemetry on an Agent

Add `TelemetryFeature` (and optionally `LoggingFeature`) to your `AgentDefinition`:

```csharp
using MyAgents.Agents;
using MyAgents.Core;

var definition = new AgentDefinition("observable-agent", [
    new TelemetryFeature(),
    new LoggingFeature(),
    new ToolsFeature(myTool)
]);
```

### Registering OpenTelemetry in `Program.cs`

Use the `AddMyAgentsTracing()` and `AddMyAgentsMetrics()` extension methods on `OpenTelemetryBuilder`:

```csharp
using MyAgents.Agents;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .AddMyAgentsTracing()   // Subscribes to "MyAgents" and "Microsoft.Extensions.AI" ActivitySources
    .AddMyAgentsMetrics()   // Subscribes to the "MyAgents" Meter
    .WithTracing(tracing => tracing.AddOtlpExporter())
    .WithMetrics(metrics => metrics.AddOtlpExporter());
```

Exported instrumentation includes:
- **Activity Source (`MyAgents` & `Microsoft.Extensions.AI`)**: End-to-end distributed spans across agent turns, middleware, LLM provider calls, and tool executions.
- **Metrics (`MyAgents`)**:
  - `agent.turn.duration_ms`: Histogram of conversational turn durations.
  - `agent.turn.tokens`: Token usage counters (`prompt`, `completion`, `total`).
  - `agent.turn.errors`: Counter of turn exceptions and failures.
