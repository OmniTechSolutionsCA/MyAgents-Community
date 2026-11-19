# MyAgents Community

**Modular, NativeAOT-ready, and provider-neutral AI agent runtime for .NET 10.**

[![NuGet Version](https://img.shields.io/badge/nuget-v1.1.0-blue)](https://www.nuget.org/packages?q=MyAgents)
[![Target Framework](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green)](https://github.com/OmniTechSolutionsCA/MyAgents-Community/blob/main/LICENSE)
[![GitHub Workflow](https://img.shields.io/badge/build-passing-brightgreen)](https://github.com/OmniTechSolutionsCA/MyAgents-Community/actions)

---

MyAgents Community is an enterprise-grade agent framework for .NET 10 built on top of `Microsoft.Extensions.AI`. It provides complete provider neutrality, compile-time NativeAOT tool generation, stateful workflow graphs, restart-recoverable durable worker execution, and hybrid RAG memory with zero vendor lock-in.

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
      <h3>🛡️ Durable Execution & Workflows</h3>
      <p style="color: var(--muted-foreground); font-size: 14px;">Background worker leasing, idempotency deduplication, stateful <code>AgentGraph&lt;TState&gt;</code> pipelines, and SQLite crash recovery via <code>MyAgents.Runtime.Workers</code>.</p>
      <a href="docs/execution/durable-runs.md">Read Durable Runs &rarr;</a>
    </div>
  </div>
  <div class="col-md-4" style="margin-bottom: 16px;">
    <div style="border: 1px solid var(--border); border-radius: var(--radius); padding: 16px; background: var(--card);">
      <h3>🔍 Hybrid RAG Memory</h3>
      <p style="color: var(--muted-foreground); font-size: 14px;">Fused SIMD dense cosine similarity (<code>TensorPrimitives.Dot</code>) and BM25 sparse lexical ranking with zero-dependency document ingestion and grounded citations.</p>
      <a href="docs/memory/hybrid-rag.md">Read Hybrid RAG &rarr;</a>
    </div>
  </div>
</div>

---

## Quick Start (C#)

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using MyAgents.Agents;
using MyAgents.Core;
using MyAgents.Runtime.Chat;

// 1. Configure services with your IChatClient and MyAgents runtime
var services = new ServiceCollection();
services.AddSingleton<IChatClient>(chatClient);
services.AddMyAgentsChatRuntime();

await using var provider = services.BuildServiceProvider();
var sessionFactory = provider.GetRequiredService<IAgentSessionFactory>();

// 2. Define an agent with tools
var tools = new ToolsFeature()
    .AddFunction(() => DateTimeOffset.UtcNow.ToString("O"),
        name: "get_time",
        description: "Returns the current UTC timestamp in ISO-8601 format.");

var definition = new AgentDefinition("assistant", [tools]);

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
| **`MyAgents.Core`** | Core interfaces (`IAgent`, `IAgentSession`, `ITool`, `AgentDefinition`, `AgentGraph<TState>`, `WorkflowHost`, approvals, middleware). |
| **`MyAgents.Runtime.Chat`** | Provider-neutral execution runtime using `Microsoft.Extensions.AI.IChatClient`. |
| **`MyAgents.Generators`** | Roslyn incremental source generator for zero-reflection `[AgentTool]` code emission. |
| **`MyAgents.Runtime.Workers`** | Long-running durable worker host with lease heartbeats, batch execution, and workflow adapters. |
| **`MyAgents.Runtime.Workers.Sqlite`** | Transactional SQLite backend for persistent durable runs. |
| **`MyAgents.Memory`** | SIMD vector store, SQLite/VectorData stores, BM25 inverted index, Reciprocal Rank Fusion, and document ingestion. |
| **`MyAgents.Mcp`** | Model Context Protocol (MCP) tool, prompt, resource, hot-reload, and skill discovery adapters. |
| **`MyAgents.Agents`** | Subagent dispatching, group chats, guardrails, structured outputs, OpenTelemetry, and model tier routing. |
| **`MyAgents.Testing`** | Deterministic in-memory test agents, fake sessions, and run backends. |

---

## Explore the Documentation

- [Getting Started Guide](docs/getting-started/quickstart.md)
- [Architecture & Execution Lifecycle](docs/core/architecture.md)
- [Structured Output & Multi-Modal](docs/core/structured-output-and-multimodal.md)
- [Prompt Registry & Versioning](docs/core/prompt-registry.md)
- [Zero-Reflection NativeAOT Tools](docs/tools/nativeaot-tools.md)
- [Model Context Protocol (MCP) & Hot-Reload](docs/tools/mcp.md)
- [State Graphs (`AgentGraph<TState>`)](docs/workflows/agent-graphs.md)
- [Checkpointed Workflows & Human-in-the-Loop](docs/workflows/checkpointed-workflows.md)
- [Orchestration Patterns (Loops, Reflection, Consensus)](docs/workflows/orchestration-patterns.md)
- [Durable Background Execution & Batches](docs/execution/durable-runs.md)
- [Hybrid RAG & Vector Stores](docs/memory/hybrid-rag.md)
- [Tool Approval & Governance](docs/governance/tool-approval.md)
- [Guardrails, Fallbacks & Dead-Letter Queues](docs/governance/guardrails-and-resilience.md)
- [Subagents & Multi-Agent Teams](docs/agents/multi-agent-and-subagents.md)
- [Unit Testing Agents](docs/testing/testing-agents.md)
- [C# API Reference](api/index.md)
