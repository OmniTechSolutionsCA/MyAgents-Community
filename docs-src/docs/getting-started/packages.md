# Package Ecosystem

MyAgents Community is built as a set of modular, decoupled NuGet packages for .NET 10. Install only what your application requires.

## Core Packages Matrix

| Package | Version | Description | Target Use Case |
| :--- | :--- | :--- | :--- |
| **`MyAgents.Core`** | `1.0.0` | Core abstractions: `IAgent`, `AgentDefinition`, `IAgentSession`, `ITool`, messages, and prompts. | Included in all applications. |
| **`MyAgents.Runtime.Chat`** | `1.0.0` | Provider-neutral conversational runtime integrating with `Microsoft.Extensions.AI.IChatClient`. | Interactive chat applications, CLI bots, web APIs. |
| **`MyAgents.Generators`** | `1.0.0` | Roslyn incremental source generator for `[AgentTool]` compile-time tool binding. | High-performance and NativeAOT trimmed applications. |
| **`MyAgents.Memory`** | `1.0.0` | SIMD dense vector similarity (`TensorPrimitives`), BM25 sparse index, Reciprocal Rank Fusion, document readers. | In-memory RAG, semantic search, long-term memory. |
| **`MyAgents.Runtime.Workers`** | `1.0.0` | Durable background worker host with lease coordination, idempotency, and retries. | Long-running asynchronous workflows, background task queues. |
| **`MyAgents.Runtime.Workers.Sqlite`** | `1.0.0` | SQLite-backed persistent run backend for durable worker execution. | On-premises and single-node durable worker storage. |
| **`MyAgents.Mcp`** | `1.0.0` | Model Context Protocol (MCP) tool adapters for stdio and HTTP/SSE transports. | Connecting to local or remote MCP servers and tools. |
| **`MyAgents.Agents`** | `1.0.0` | High-level orchestrations: subagents, group chat, token cost tracking, and model routing. | Complex multi-agent teams, hierarchical delegation. |
| **`MyAgents.Testing`** | `1.0.0` | In-memory test runtime, deterministic mock agents, and turn assertions. | Unit tests and integration test suites. |

---

## Installation Commands

### Conversational Chat Agent
```bash
dotnet add package MyAgents.Core
dotnet add package MyAgents.Runtime.Chat
```

### High-Performance NativeAOT Agent with Compile-Time Tools
```bash
dotnet add package MyAgents.Core
dotnet add package MyAgents.Runtime.Chat
dotnet add package MyAgents.Generators
```

### Retrieval-Augmented Generation (RAG)
```bash
dotnet add package MyAgents.Core
dotnet add package MyAgents.Runtime.Chat
dotnet add package MyAgents.Memory
```

### Durable Worker Process (Background Queue)
```bash
dotnet add package MyAgents.Core
dotnet add package MyAgents.Runtime.Workers
dotnet add package MyAgents.Runtime.Workers.Sqlite
```

### External MCP Tools Integration
```bash
dotnet add package MyAgents.Core
dotnet add package MyAgents.Runtime.Chat
dotnet add package MyAgents.Mcp
```

### Multi-Agent Workflows & Cost Routing
```bash
dotnet add package MyAgents.Core
dotnet add package MyAgents.Runtime.Chat
dotnet add package MyAgents.Agents
```

### Unit Testing Suite
```bash
dotnet add package MyAgents.Testing
```
