# Sessions & Turns

An agent interaction in MyAgents Community consists of **Sessions** (`IAgentSession`) and **Turns** (`AgentTurnContext` / `AgentTurnResult`).

## Understanding Sessions

An `IAgentSession` manages an ongoing interaction with an agent definition. It provides:
1. **Isolated Pipeline & Capabilities**: Assembled prompts, tools, middleware, and session-scoped services (`AgentAssembly.SessionServices`).
2. **Resource Lifetime**: Implements `IAsyncDisposable` and `IDisposable` so session-owned resources (such as local MCP child processes or network transports) are cleaned up deterministically.
3. **Turn Invocation**: Blocking (`InvokeAsync`), strongly-typed structured (`InvokeAsync<T>`), and streaming (`InvokeStreamingAsync`) methods.

### Creating a Session

```csharp
using Microsoft.Extensions.DependencyInjection;
using MyAgents.Core;

var factory = serviceProvider.GetRequiredService<IAgentSessionFactory>();

// Synchronous session creation
await using var session = factory.CreateSession("user-1001-conversation", definition);

// Or asynchronous session creation (recommended when using async features like McpFeature)
await using var asyncSession = await ((DefaultAgentSessionFactory)factory)
    .CreateSessionAsync("user-1002-conversation", definition, cancellationToken);
```

---

## Executing Conversational Turns

A **turn** represents a complete input-to-output cycle: the caller supplies one or more `AgentMessage` instances, and the runtime executes the middleware pipeline and any model-requested tool round-trips to produce an `AgentTurnResult`.

### Standard Invocation (`InvokeAsync`)

Use `InvokeAsync` when you want the complete final response and telemetry once all tool calls and reasoning steps finish:

```csharp
var input = AgentMessage.FromText("user", "Summarize our quarterly metrics.");
AgentTurnResult turn = await session.InvokeAsync([input], cancellationToken);

foreach (var message in turn.Output)
{
    Console.WriteLine($"[{message.Role}]: {message.Content}");
}

// Inspect turn telemetry
Console.WriteLine($"Duration: {turn.Telemetry.TurnDuration.TotalMilliseconds:F0}ms");
Console.WriteLine($"Tool Calls: {turn.Telemetry.ToolCalls}");
Console.WriteLine($"Tokens: {turn.Telemetry.PromptTokens} in / {turn.Telemetry.CompletionTokens} out ({turn.Telemetry.TotalTokens} total)");
```

You can also customize the `AgentTurnContext` per turn using the `configureContext` overload:

```csharp
var turn = await session.InvokeAsync(
    [AgentMessage.FromText("user", "Return the result as a JSON object.")],
    configureContext: ctx => ctx.Capabilities.Set(AgentResponseFormat.JsonObject),
    cancellationToken: cancellationToken);
```

### Streaming Invocation (`InvokeStreamingAsync`)

Use `InvokeStreamingAsync` for low-latency interactive applications where response chunks should be rendered as they arrive:

```csharp
var input = AgentMessage.FromText("user", "Draft an email to the engineering team.");

await foreach (AgentTurnResult chunk in session.InvokeStreamingAsync([input], cancellationToken))
{
    foreach (var message in chunk.Output)
    {
        Console.Write(message.Content);
    }
}
Console.WriteLine();
```

If you want to fold streamed chunks into a single consolidated `AgentTurnResult` (summing token counts and concatenating assistant output), use `AgentOutputAccumulator.Fold`:

```csharp
var chunks = new List<AgentTurnResult>();
await foreach (var chunk in session.InvokeStreamingAsync([input], cancellationToken))
{
    chunks.Add(chunk);
}

AgentTurnResult? fullResult = AgentOutputAccumulator.Fold(chunks);
```

---

## Messages, Metadata & Roles

`AgentMessage` is an immutable record supporting correlation metadata and multi-modal content parts:

```csharp
// Simple text message
var userMsg = AgentMessage.FromText(
    role: "user",
    text: "Hello!",
    correlationId: "corr-101",
    threadId: "thread-42",
    senderId: "user-alice");

// Multi-modal message with text and image bytes
var multiModalMsg = AgentMessage.FromParts(
    role: "user",
    parts:
    [
        new TextAgentContent("What is shown in this architecture diagram?"),
        new ImageAgentContent(imageBytes, "image/png")
    ],
    textSummary: "What is shown in this architecture diagram?");
```

---

## Context Window Management & Trimming

To prevent long-running conversations from exceeding model token limits, add `ContextWindowFeature` (or `ContextWindowMiddleware` directly). System messages are always preserved at the start of the conversation while older non-system messages are trimmed:

### Sliding Message Count Window

```csharp
using MyAgents.Agents;
using MyAgents.Core;

var definition = new AgentDefinition("chat-agent", [
    // Retains all system prompts plus the most recent 10 user/assistant messages
    new ContextWindowFeature(maxMessages: 10)
]);
```

### Token Budget Trimming

Chain `.WithTokenBudget(...)` or pass a `TokenBudgetTrimmer` to trim messages based on a maximum token budget:

```csharp
using MyAgents.Agents;
using MyAgents.Core;
using MyAgents.Memory;

var definition = new AgentDefinition("token-bounded-agent", [
    new ContextWindowFeature(maxMessages: 50)
        .WithTokenBudget(maxTokens: 4000, tokenCounter: ApproximateTokenCounter.Instance)
]);
```
