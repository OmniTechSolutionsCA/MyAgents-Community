# MyAgents Community

**Build modular, tool-using AI agents with .NET 10.**

MyAgents Community is a modular, high-performance, and provider-neutral agent runtime for .NET 10. Connect any AI model through Microsoft.Extensions.AI's `IChatClient`, compose agent behaviors from reusable features, and execute tool invocations with NativeAOT safety, durable restart recovery, and hybrid memory retrieval.

### Quick Start (C#)

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using MyAgents.Core;
using MyAgents.Core.Tools;
using MyAgents.Runtime.Chat;

// 1. Configure services with your IChatClient and MyAgents runtime
var services = new ServiceCollection();
services.AddSingleton<IChatClient>(chatClient);
services.AddMyAgentsChatRuntime();

await using var provider = services.BuildServiceProvider();
var sessionFactory = provider.GetRequiredService<IAgentSessionFactory>();

// 2. Define an agent with system prompts and tools
var definition = new AgentDefinition("assistant", [
    AgentFeature.Create(assembly =>
    {
        assembly.Prompts.AddSystem("You are a helpful assistant.");
        assembly.Tools.Add(DelegateTool.Create("get_time", () => DateTimeOffset.UtcNow.ToString("O")));
    })
]);

// 3. Create a session and run turns
await using var session = sessionFactory.CreateSession("session-1", definition);
var turn = await session.InvokeAsync([AgentMessage.FromText("user", "What time is it in UTC?")]);

foreach (var message in turn.Output)
{
    Console.WriteLine(message.Content);
}
```

[Read the Guides](docs/index.md) to explore NativeAOT tools, durable execution, and hybrid RAG memory.
