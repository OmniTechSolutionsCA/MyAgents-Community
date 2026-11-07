# Unit Testing Agents

Testing conversational AI applications can be challenging because real LLM API calls are nondeterministic, slow, and incur token costs.

The `MyAgents.Testing` package provides deterministic, in-memory mocks for testing agent definitions, prompt templates, and tool pipelines without hitting external APIs.

## Installation

```bash
dotnet add package MyAgents.Testing
```

---

## 1. Using `InMemoryAgent`

`InMemoryAgent` is a deterministic mock agent that returns pre-queued responses and records all incoming turn contexts:

```csharp
using MyAgents.Core;
using MyAgents.Testing;
using Xunit;

public sealed class AgentWorkflowTests
{
    [Fact]
    public async Task Agent_Returns_Expected_Sequence()
    {
        // 1. Arrange a mock agent with queued responses
        var mockAgent = new InMemoryAgent("MockReviewer")
            .EnqueueResponse("Step 1: Code verified.")
            .EnqueueResponse("Step 2: Ready for production.");

        var context1 = new AgentTurnContext("session-1", [AgentMessage.FromText("user", "Check PR #42")]);
        var context2 = new AgentTurnContext("session-1", [AgentMessage.FromText("user", "Proceed?")]);

        // 2. Act
        var result1 = await mockAgent.InvokeAsync(context1);
        var result2 = await mockAgent.InvokeAsync(context2);

        // 3. Assert
        Assert.Equal("Step 1: Code verified.", result1.Output[0].Content);
        Assert.Equal("Step 2: Ready for production.", result2.Output[0].Content);
        Assert.Equal(2, mockAgent.InvocationCount);
    }
}
```

---

## 2. Testing with `InMemoryAgentRuntime`

To test the entire session and feature pipeline (including tool execution and feature interceptors) with a fake model backend:

```csharp
using MyAgents.Core;
using MyAgents.Testing;
using Xunit;

public sealed class SessionTurnTests
{
    [Fact]
    public async Task Session_Applies_SystemPrompts_And_Tools()
    {
        var runtime = new InMemoryAgentRuntime();
        runtime.SetModelResponse("The current weather is sunny.");

        var definition = new AgentDefinition("assistant", [
            AgentFeature.Create(assembly =>
            {
                assembly.Prompts.AddSystem("You are a helpful assistant.");
            })
        ]);

        var session = runtime.CreateSession("test-session", definition);
        var turnResult = await session.InvokeAsync([AgentMessage.FromText("user", "Hello")]);

        Assert.Equal("The current weather is sunny.", turnResult.Output[0].Content);
    }
}
```

---

## 3. Testing Durable Run Handlers

Use `InMemoryAgentRunBackend` to test durable worker handlers without spinning up a real SQLite database file:

```csharp
[Fact]
public async Task DurableRun_Deduplicates_Duplicate_Keys()
{
    var backend = new InMemoryAgentRunBackend();

    var request = new AgentRunRequest(
        HandlerId: "report-generator",
        InputJson: "{}",
        IdempotencyKey: "unique-key-101");

    var firstSubmission = await backend.SubmitAsync(request);
    var secondSubmission = await backend.SubmitAsync(request);

    Assert.False(firstSubmission.WasDeduplicated);
    Assert.True(secondSubmission.WasDeduplicated);
    Assert.Equal(firstSubmission.RunId, secondSubmission.RunId);
}
```
