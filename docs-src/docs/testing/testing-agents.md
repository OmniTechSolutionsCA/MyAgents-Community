# Unit Testing Agents, Sessions & Durable Runs

Testing conversational AI applications against live LLM endpoints is slow, nondeterministic, and expensive. The `MyAgents.Testing` package provides deterministic, in-memory test doubles for agents, sessions, middleware pipelines, and durable run backends.

## Installation

```bash
dotnet add package MyAgents.Testing
```

---

## 1. Testing Agents with `InMemoryAgent`

`InMemoryAgent` implements `IAgent` with a FIFO queue of scripted responses and records every `AgentTurnContext` it receives:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MyAgents.Core;
using MyAgents.Testing;
using NUnit.Framework;

[TestFixture]
public sealed class AgentTests
{
    [Test]
    public async Task InMemoryAgent_Returns_Queued_Responses_In_Order()
    {
        // 1. Arrange a deterministic agent with queued responses and token counts
        using var agent = new InMemoryAgent("MockReviewer")
            .EnqueueResponse("Step 1: Static analysis passed.", promptTokens: 40, completionTokens: 12)
            .EnqueueResponse("Step 2: Ready to merge.", promptTokens: 55, completionTokens: 8)
            .WithFallbackResponse("Default fallback response");

        var services = new AgentTurnServices(new ServiceCollection().BuildServiceProvider());
        var context1 = new AgentTurnContext("session-1", [AgentMessage.FromText("user", "Check PR #42")], services);
        var context2 = new AgentTurnContext("session-1", [AgentMessage.FromText("user", "Approve?")], services);

        // 2. Act
        var result1 = await agent.InvokeAsync(context1);
        var result2 = await agent.InvokeAsync(context2);

        // 3. Assert
        Assert.That(result1.Output[0].Content, Is.EqualTo("Step 1: Static analysis passed."));
        Assert.That(result1.Telemetry.TotalTokens, Is.EqualTo(52));
        Assert.That(result2.Output[0].Content, Is.EqualTo("Step 2: Ready to merge."));
        Assert.That(agent.InvocationCount, Is.EqualTo(2));
        Assert.That(agent.ReceivedContexts, Has.Count.EqualTo(2));
    }
}
```

---

## 2. Testing Middleware Pipelines with `FakeAgentSession`

`FakeAgentSession` wraps an `InMemoryAgent` inside an `IAgentSession` so you can unit-test middleware (`CircuitBreakerMiddleware`, `ContextWindowMiddleware`, `GuardrailMiddleware`, etc.) in isolation:

```csharp
using MyAgents.Agents;
using MyAgents.Core;
using MyAgents.Testing;
using NUnit.Framework;

[Test]
public async Task FakeAgentSession_Executes_Middleware_Pipeline()
{
    var guardrail = new GuardrailMiddleware([new BlocklistGuardrailPolicy(["secret-token"])]);

    await using var session = FakeAgentSession.CreateWithMiddleware("test-session", guardrail)
        .EnqueueResponse("Here is the leaked secret-token value.");

    var turn = await session.InvokeAsync([AgentMessage.FromText("user", "Show config")]);

    // GuardrailMiddleware intercepts the response and substitutes the safe fallback message
    Assert.That(turn.Output[0].Content, Does.Not.Contain("secret-token"));
    Assert.That(session.InvocationCount, Is.EqualTo(1));
}
```

---

## 3. Testing Full Agent Definitions with `InMemoryAgentRuntime`

Use `InMemoryAgentRuntime` alongside `DefaultAgentSessionFactory` (or `services.AddMyAgents(runtime)`) to test complete `AgentDefinition` compositions, multi-agent `GroupChat`, or `AgentGraph<TState>` workflows:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MyAgents.Core;
using MyAgents.Testing;
using NUnit.Framework;

[Test]
public async Task InMemoryAgentRuntime_Creates_And_Configures_Named_Agents()
{
    var runtime = new InMemoryAgentRuntime()
        .ForAgent("researcher", agent => agent.WithFallbackResponse("Research findings complete."))
        .ForAgent("writer", agent => agent.WithFallbackResponse("Final article draft."));

    var services = new ServiceCollection()
        .AddMyAgents(runtime)
        .BuildServiceProvider();

    var factory = services.GetRequiredService<IAgentSessionFactory>();

    await using var session = factory.CreateSession(
        "session-1",
        new AgentDefinition("researcher", []));

    var turn = await session.InvokeAsync([AgentMessage.FromText("user", "Start research")]);

    Assert.That(turn.Output[0].Content, Is.EqualTo("Research findings complete."));
    Assert.That(runtime.GetAgent("researcher").InvocationCount, Is.EqualTo(1));
}
```

---

## 4. Testing Durable Runs with `InMemoryAgentRunBackend`

`InMemoryAgentRunBackend` implements `IAgentRunBackend`, `IAgentRunBatchCatalog`, and `IAgentRunBatchController` in memory and accepts a custom `TimeProvider` (such as `FakeTimeProvider`) for deterministic lease expiration and retry testing:

```csharp
using MyAgents.Core.Execution;
using MyAgents.Testing;
using NUnit.Framework;

[Test]
public async Task InMemoryAgentRunBackend_Deduplicates_IdempotencyKeys()
{
    var backend = new InMemoryAgentRunBackend(TimeProvider.System);

    var request = new AgentRunRequest(
        HandlerId: "report-generator",
        InputJson: """{"quarter":"Q3"}""",
        IdempotencyKey: "q3-report-2026",
        ScopeKey: "tenant-1");

    var first = await backend.SubmitAsync(request);
    var second = await backend.SubmitAsync(request);

    Assert.Multiple(() =>
    {
        Assert.That(first.WasDeduplicated, Is.False);
        Assert.That(second.WasDeduplicated, Is.True);
        Assert.That(second.RunId, Is.EqualTo(first.RunId));
    });
}
```
