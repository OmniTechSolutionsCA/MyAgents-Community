# Sessions & Turns

An agent interaction in MyAgents Community consists of **Sessions** and **Turns**.

## Understanding Sessions

An `IAgentSession` manages an ongoing conversation between a user and an agent. It provides:
1. **Isolated State**: Conversation history, variables, and session-owned resources.
2. **Resource Lifetime**: Any tools or connections opened during a session (such as local MCP child processes or database connections) are automatically disposed when the session is disposed.
3. **Turn Invocation**: Synchronous and streaming invocation methods.

### Creating a Session

```csharp
using MyAgents.Core;

// Resolve the session factory from DI
var factory = serviceProvider.GetRequiredService<IAgentSessionFactory>();

// Create a named session with a specific definition
await using var session = factory.CreateSession("user-1001-conversation", definition);
```

---

## Executing Conversational Turns

A **turn** represents a single input/output cycle: the user supplies one or more messages, and the agent processes them (including zero or more tool calls) to produce an output.

### Standard Invocation (`InvokeAsync`)

Use `InvokeAsync` when you want the complete final response once all tool calls and reasoning steps are finished:

```csharp
var input = AgentMessage.FromText("user", "Summarize our quarterly metrics.");
AgentTurnResult turn = await session.InvokeAsync([input], cancellationToken);

foreach (var message in turn.Output)
{
    Console.WriteLine($"[{message.Role}]: {message.Content}");
}
```

### Streaming Invocation (`InvokeStreamingAsync`)

Use `InvokeStreamingAsync` for low-latency interactive applications (like chat web UIs or CLI spinners) where tokens should be rendered as they arrive:

```csharp
var input = AgentMessage.FromText("user", "Draft an email to the engineering team.");

await foreach (AgentTurnResult update in session.InvokeStreamingAsync([input], cancellationToken))
{
    foreach (var message in update.Output)
    {
        Console.Write(message.Content);
    }
}
Console.WriteLine();
```

> [!TIP]
> Even during streaming, MyAgents Community maintains strict tool approval safety. If a tool requires human approval or triggers a policy rejection, the stream handles the pause/rejection cleanly before continuing.

---

## Session Messages & Roles

MyAgents Community uses strongly-typed `AgentMessage` records with standard roles:

- `AgentMessage.FromText("user", "Hello!")`: User prompt.
- `AgentMessage.FromText("assistant", "How can I help?")`: Model output.
- `AgentMessage.FromText("system", "Instructions here.")`: System prompt.
- `AgentMessage.FromToolResult(toolCallId, "42")`: Tool execution result payload.

---

## Session History & Trimming

To prevent sessions from exceeding LLM context windows in long-running conversations, you can register a `ContextWindowFeature`:

```csharp
using MyAgents.Agents;

var definition = new AgentDefinition("chat-agent", [
    new ContextWindowFeature(new ContextWindowOptions
    {
        MaxTokens = 8000,
        PreserveSystemPrompt = true,
        KeepLatestUserMessages = 4
    })
]);
```
