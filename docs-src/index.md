# MyAgents Community

**Modular, NativeAOT-ready, and provider-neutral AI agent runtime for .NET 10.**

[![NuGet Version](https://img.shields.io/badge/nuget-v1.0.0-blue)](https://www.nuget.org/packages?q=MyAgents)
[![Target Framework](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green)](https://github.com/OmniTechSolutionsCA/MyAgents-Community/blob/main/LICENSE)
[![GitHub Workflow](https://img.shields.io/badge/build-passing-brightgreen)](https://github.com/OmniTechSolutionsCA/MyAgents-Community/actions)

---

MyAgents Community is an enterprise-grade agent framework for .NET 10 built on top of `Microsoft.Extensions.AI`. It provides complete provider neutrality, compile-time NativeAOT tool generation, restart-recoverable durable worker execution, and hybrid RAG memory with zero vendor lock-in.

<div class="row" style="margin-top: 24px; margin-bottom: 24px;">
  <div class="col-md-4" style="margin-bottom: 16px;">
    <div style="border: 1px solid var(--border); border-radius: var(--radius); padding: 16px; background: var(--card);">
      <h3>🚀 NativeAOT Ready</h3>
      <p style="color: var(--muted-foreground); font-size: 14px;">Generate compile-time tool schemas and bindings with <code>MyAgents.Generators</code>. Zero runtime reflection, zero trimming warnings, and near-instant cold starts.</p>
      <a href="docs/tools/nativeaot-tools.md">Read NativeAOT Guide &rarr;</a>
    </div>
  </div>
  <div class="col-md-4" style="margin-bottom: 16px;">
    <div style="border: 1px solid var(--border); border-radius: var(--radius); padding: 16px; background: var(--card);">
      <h3>🛡️ Durable Execution</h3>
      <p style="color: var(--muted-foreground); font-size: 14px;">Background worker leasing, idempotency deduplication, and automatic crash recovery with SQLite persistence via <code>MyAgents.Runtime.Workers</code>.</p>
      <a href="docs/execution/durable-runs.md">Read Durable Runs &rarr;</a>
    </div>
  </div>
  <div class="col-md-4" style="margin-bottom: 16px;">
    <div style="border: 1px solid var(--border); border-radius: var(--radius); padding: 16px; background: var(--card);">
      <h3>🔍 Hybrid RAG Memory</h3>
      <p style="color: var(--muted-foreground); font-size: 14px;">Fused SIMD dense cosine similarity (<code>TensorPrimitives</code>) and BM25 sparse lexical ranking with zero external vector database dependencies.</p>
      <a href="docs/memory/hybrid-rag.md">Read Hybrid RAG &rarr;</a>
    </div>
  </div>
</div>

---

## Quick Start (C#)

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

---

## Package Ecosystem Matrix

Install only the modules your workload needs:

| Package | Purpose |
| :--- | :--- |
| **`MyAgents.Core`** | Core interfaces (`IAgent`, `IAgentSession`, `ITool`, `AgentDefinition`, messages). |
| **`MyAgents.Runtime.Chat`** | Provider-neutral execution runtime using `Microsoft.Extensions.AI.IChatClient`. |
| **`MyAgents.Generators`** | Roslyn incremental source generator for zero-reflection `[AgentTool]` code emission. |
| **`MyAgents.Runtime.Workers`** | Long-running durable worker host with lease heartbeats and idempotency. |
| **`MyAgents.Runtime.Workers.Sqlite`** | SQLite backend for persistent durable runs. |
| **`MyAgents.Memory`** | SIMD vector store, BM25 inverted index, Reciprocal Rank Fusion, document readers. |
| **`MyAgents.Mcp`** | Model Context Protocol (MCP) tool integration over stdio and HTTP/SSE. |
| **`MyAgents.Agents`** | Subagent dispatching, group chats, token cost accounting, and model tier routing. |
| **`MyAgents.Testing`** | Deterministic in-memory test agents and session runtimes. |

---

## Explore the Documentation

- [Getting Started Guide](docs/getting-started/quickstart.md)
- [Architecture & Execution Lifecycle](docs/core/architecture.md)
- [Zero-Reflection NativeAOT Tools](docs/tools/nativeaot-tools.md)
- [Model Context Protocol (MCP)](docs/tools/mcp.md)
- [Durable Background Execution](docs/execution/durable-runs.md)
- [Hybrid RAG & Ingestion](docs/memory/hybrid-rag.md)
- [Tool Approval & Governance](docs/governance/tool-approval.md)
- [Subagents & Multi-Agent Teams](docs/agents/multi-agent-and-subagents.md)
- [Unit Testing Agents](docs/testing/testing-agents.md)
