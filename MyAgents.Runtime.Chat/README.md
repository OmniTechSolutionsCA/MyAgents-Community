# MyAgents.Runtime.Chat

The Community chat runtime connects MyAgents agent definitions to any provider
that implements `Microsoft.Extensions.AI.IChatClient`. It runs chat turns,
executes declared MyAgents tools, applies tool approval policies, and reports
tool observations and token usage through the core contracts.

## Register a provider and runtime

Register the provider-specific `IChatClient` implementation, then add the
MyAgents runtime and services:

```csharp
var services = new ServiceCollection();
services.AddSingleton<IChatClient>(chatClient);
services.AddMyAgentsChatRuntime(options =>
{
    options.DefaultModelId = "your-model-id";
    options.MaxToolRoundTrips = 8;
});

await using var provider = services.BuildServiceProvider();
var sessions = provider.GetRequiredService<IAgentSessionFactory>();
await using var session = sessions.CreateSession(
    "conversation-1",
    new AgentDefinition("assistant", []));
var turn = await session.InvokeAsync([AgentMessage.FromText("user", "Hello!")]);
Console.WriteLine(turn.Output[0].Content);
```

Create a session through `IAgentSessionFactory` with an `AgentDefinition`, then
invoke it with user messages. Agent features provide prompts and tools; a
`ModelFeature` or `AgentModelCapability` can select a model for that agent.

Tool calls pass through `IToolApprovalPolicy` before execution. Configure an
approval policy appropriate to your application when tools can change data or
perform other sensitive actions.

If no approval policy is registered, MyAgents' approval evaluator allows the
call by default. The runtime owns tool dispatch so that approval is checked;
do not add provider middleware that automatically invokes function tools.

`InvokeStreamingAsync` currently yields one completed turn after buffering the
chat and tool loop. It does not yet forward provider token-by-token updates.

## Scope

This package provides the in-process chat and tool runtime. It does not include
the proprietary consolidated `MyAgents.Runtime`, graph persistence, workspace
orchestration, queued/container execution, hosted services, or a provider-specific
chat client. Add a provider package that implements `IChatClient` separately.
