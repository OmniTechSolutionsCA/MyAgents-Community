# Quick Start Guide

In this guide, you will build and run your first tool-calling AI agent using MyAgents Community in under 5 minutes.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An API key for an LLM provider (e.g., OpenAI, Azure OpenAI, Ollama, or Anthropic) or an in-memory mock client.

## 1. Create a New Console Application

```bash
dotnet new console -n MyFirstAgent
cd MyFirstAgent
```

## 2. Add Required NuGet Packages

Install the core abstractions, chat runtime, and your preferred AI client:

```bash
dotnet add package MyAgents.Core
dotnet add package MyAgents.Runtime.Chat
dotnet add package Microsoft.Extensions.AI.OpenAI
```

> [!NOTE]
> MyAgents Community is completely provider-neutral. It interfaces directly with any `Microsoft.Extensions.AI.IChatClient` implementation (OpenAI, Azure OpenAI, Ollama, GitHub Models, AWS Bedrock, etc.).

## 3. Build Your First Agent

Replace `Program.cs` with the following code:

```csharp
using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using MyAgents.Core;
using MyAgents.Core.Tools;
using MyAgents.Runtime.Chat;
using OpenAI;

// 1. Configure the AI Chat Client
var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "your-api-key";
var chatClient = new OpenAIClient(apiKey).GetChatClient("gpt-4o-mini");

// 2. Register MyAgents runtime in Dependency Injection
var services = new ServiceCollection();
services.AddSingleton<IChatClient>(chatClient);
services.AddMyAgentsChatRuntime();

await using var serviceProvider = services.BuildServiceProvider();
var sessionFactory = serviceProvider.GetRequiredService<IAgentSessionFactory>();

// 3. Define the Agent and its capabilities
var weatherTool = DelegateTool.Create(
    name: "get_weather",
    description: "Gets the current temperature and conditions for a given city.",
    handler: ([Description("The city name")] string city) =>
    {
        return $"Weather in {city}: 21°C (70°F), Clear Skies";
    });

var definition = new AgentDefinition("weather-assistant", [
    AgentFeature.Create(assembly =>
    {
        assembly.Prompts.AddSystem("You are a concise, helpful assistant. Always use tools to answer weather queries.");
        assembly.Tools.Add(weatherTool);
    })
]);

// 4. Create an isolated conversation session
await using var session = sessionFactory.CreateSession("session-demo-1", definition);

// 5. Invoke the agent
Console.WriteLine("User: What's the weather in Vancouver?");
var result = await session.InvokeAsync([
    AgentMessage.FromText("user", "What's the weather in Vancouver?")
]);

// 6. Display the assistant's response
foreach (var message in result.Output)
{
    Console.WriteLine($"Assistant: {message.Content}");
}
```

## 4. Run the Application

```bash
dotnet run
```

### Expected Output

```text
User: What's the weather in Vancouver?
Assistant: The current weather in Vancouver is 21°C (70°F) with clear skies.
```

---

## Next Steps

- Explore [Package Ecosystem](packages.md) to discover additional modules like RAG, background workers, and source generators.
- Learn about [NativeAOT Tools](../tools/nativeaot-tools.md) for compile-time reflection-free performance.
- Learn about [Durable Runs](../execution/durable-runs.md) for crash-resilient background processing.
