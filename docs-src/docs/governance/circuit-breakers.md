# Circuit Breakers & Observability

To protect downstream services from runaway loops, sudden model hallucinations, and cascading failures, MyAgents Community provides built-in circuit breakers, execution observers, and OpenTelemetry metrics.

## 1. Tool Circuit Breaker

When an external tool dependency fails repeatedly (e.g. database timeout or third-party API outage), the `CircuitBreakerMiddleware` trips open, immediately rejecting further tool calls before they hit the failing service:

```csharp
using MyAgents.Core.Middleware;

var circuitBreaker = new CircuitBreakerMiddleware(new CircuitBreakerOptions
{
    FailureThreshold = 3,                       // Trip after 3 consecutive failures
    BreakDuration = TimeSpan.FromSeconds(30),   // Stay open for 30 seconds
    HandledExceptions = [typeof(HttpRequestException), typeof(TimeoutException)]
});
```

When tripped, the circuit breaker returns a structured error to the model so the agent can adapt or inform the user rather than hanging.

---

## 2. Tool Observers

Tool observers allow you to audit, log, or measure tool execution without altering the tool's business logic:

```csharp
using MyAgents.Core.Tools;

public sealed class SecurityAuditToolObserver : IToolObserver
{
    private readonly ILogger _logger;

    public SecurityAuditToolObserver(ILogger logger)
    {
        _logger = logger;
    }

    public void OnToolInvoking(ToolInvokingContext context)
    {
        _logger.LogInformation("Tool {ToolName} invoking with args: {Args}", context.ToolName, context.ArgumentsJson);
    }

    public void OnToolInvoked(ToolInvokedContext context)
    {
        _logger.LogInformation("Tool {ToolName} completed in {ElapsedMs}ms. Success: {IsSuccess}",
            context.ToolName, context.Duration.TotalMilliseconds, context.Result.IsSuccess);
    }
}
```

Register observers using `ToolObserverFeature`:

```csharp
var definition = new AgentDefinition("audited-agent", [
    new ToolObserverFeature(new SecurityAuditToolObserver(logger))
]);
```

---

## 3. OpenTelemetry Metrics & Tracing

`MyAgents.Agents` integrates natively with `System.Diagnostics.DiagnosticSource` and OpenTelemetry:

```csharp
services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSource("MyAgents.Community");
        tracing.AddOtlpExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics.AddMeter("MyAgents.Community");
        metrics.AddOtlpExporter();
    });
```

Exported metrics include:
- `agent.turn.duration_ms`: Duration of conversational turns.
- `agent.tool.invocations_total`: Counter of tool calls tagged by tool name and status.
- `agent.tokens.cost_usd`: Real-time token usage and cost metrics per model tier.
