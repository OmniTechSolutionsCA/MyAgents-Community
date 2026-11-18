# Quick Start Guide

In this guide, you will build and run your first tool-calling AI agent using MyAgents Community in under 5 minutes.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An API key for an LLM provider (e.g., OpenAI, Azure OpenAI, Ollama, or Anthropic) or an in-memory test runtime.

## 1. Create a New Console Application

```bash
dotnet new console -n MyFirstAgent
cd MyFirstAgent
```

## 2. Add Required NuGet Packages

Install the core contracts, reusable agent features, chat runtime, and your preferred AI client:

```bash
dotnet add package MyAgents.Core
dotnet add package MyAgents.Agents
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
using MyAgents.Agents;
using MyAgents.Core;
using MyAgents.Core.Prompts;
using MyAgents.Core.Tools;
using MyAgents.Runtime.Chat;
using OpenAI;

// 1. Configure the AI Chat Client
var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "your-api-key";
IChatClient chatClient = new OpenAIClient(apiKey)
    .GetChatClient("gpt-4o-mini")
    .AsIChatClient();

// 2. Register MyAgents runtime in Dependency Injection
var services = new ServiceCollection();
services.AddSingleton<IChatClient>(chatClient);
services.AddMyAgentsChatRuntime(options =>
{
    options.DefaultModelId = "gpt-4o-mini";
    options.MaxToolRoundTrips = 5;
});

await using var serviceProvider = services.BuildServiceProvider();
var sessionFactory = serviceProvider.GetRequiredService<IAgentSessionFactory>();

// 3. Define the Agent and its capabilities using composable features
var promptRegistry = new PromptRegistry()
    .Register(new PromptTemplateDefinition(
        Name: "system.weather",
        Version: "1.0",
        Template: "You are a concise, helpful assistant. Always use tools to answer weather queries.",
        Description: "Weather assistant system prompt"));

var toolsFeature = new ToolsFeature()
    .AddFunction(
        ([Description("The city name")] string city) =>
            $"Weather in {city}: 21°C (70°F), Clear Skies",
        name: "get_weather",
        description: "Gets the current temperature and conditions for a given city.");

var definition = new AgentDefinition("weather-assistant", [
    new PromptRegistryFeature(promptRegistry, systemTemplateName: "system.weather"),
    toolsFeature
]);

// 4. Create an isolated conversation session
await using var session = sessionFactory.CreateSession("session-demo-1", definition);

// 5. Invoke the agent
Console.WriteLine("User: What's the weather in Vancouver?");
var result = await session.InvokeAsync([
    AgentMessage.FromText("user", "What's the weather in Vancouver?")
]);

// 6. Display the assistant's response and turn telemetry
foreach (var message in result.Output)
{
    Console.WriteLine($"Assistant: {message.Content}");
}

Console.WriteLine($"Tool calls executed: {result.Telemetry.ToolCalls} (Duration: {result.Telemetry.TurnDuration.TotalMilliseconds:F0}ms)");
```

## 4. Run the Application

```bash
dotnet run
```

### Expected Output

```text
User: What's the weather in Vancouver?
Assistant: The current weather in Vancouver is 21°C (70°F) with clear skies.
Tool calls executed: 1 (Duration: 420ms)
```

---

## Authoring a Custom `IAgentFeature`

In addition to built-in features like `ToolsFeature` and `PromptRegistryFeature`, you can group related prompts, tools, capabilities, and middleware into a dedicated `IAgentFeature`:

```csharp
using MyAgents.Core;
using MyAgents.Core.Tools;

public sealed class UtcClockFeature : IAgentFeature
{
    public void Contribute(AgentAssembly assembly)
    {
        assembly.Prompts.AddSystem("Use the get_current_utc_time tool when asked for the current UTC time.");
        assembly.Tools.Add(DelegateTool.Create(
            () => DateTimeOffset.UtcNow.ToString("O"),
            name: "get_current_utc_time",
            description: "Returns the current UTC date and time in ISO-8601 format."));
    }
}
```

---

## Next Steps

- Explore [Package Ecosystem](packages.md) to discover additional modules like RAG, background workers, and source generators.
- Learn about [NativeAOT Tools](../tools/nativeaot-tools.md) for compile-time reflection-free performance.
- Learn about [Agent State Graphs](../workflows/agent-graphs.md) and [Durable Runs](../execution/durable-runs.md) for crash-resilient background processing.
