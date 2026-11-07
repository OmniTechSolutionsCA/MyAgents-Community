# Documentation Guides

Welcome to the MyAgents Community documentation. Explore guides, architectural patterns, and code samples across the entire platform.

---

## 🚀 Getting Started
- **[Quick Start](getting-started/quickstart.md)**: Build and run your first tool-calling AI agent in under 5 minutes.
- **[Package Ecosystem](getting-started/packages.md)**: Explore the 9 modular packages for .NET 10.

---

## 🏗️ Core Concepts
- **[Architecture & Execution Lifecycle](core/architecture.md)**: Understand how session factories, feature pipelines, and turn loops interact with `IChatClient`.
- **[Sessions & Turns](core/sessions-and-turns.md)**: Manage isolated conversation state, streaming responses, and context windows.

---

## ⚡ Tools & Extensibility
- **[NativeAOT Tools](tools/nativeaot-tools.md)**: Generate zero-reflection, compile-time tools using `MyAgents.Generators`.
- **[Delegate & Custom Tools](tools/delegate-tools.md)**: Create tools from in-process C# lambdas or custom `ITool` classes.
- **[Model Context Protocol (MCP)](tools/mcp.md)**: Connect your agent to external tools and data sources via stdio or HTTP/SSE transports.

---

## 🛡️ Durable Execution & Resilience
- **[Durable Agent Runs](execution/durable-runs.md)**: Host crash-resilient, restart-recoverable workloads using SQLite leases and background worker services.

---

## 🔍 Memory & Retrieval-Augmented Generation (RAG)
- **[Hybrid RAG](memory/hybrid-rag.md)**: Fuse dense SIMD cosine similarity (`TensorPrimitives`) with BM25 sparse lexical ranking.
- **[Episodic Memory](memory/episodic-memory.md)**: Enable cross-session recall and automated milestone consolidation.
- **[Document Ingestion](memory/document-ingestion.md)**: Ingest and chunk Markdown, HTML, Plain Text, and JSON with zero third-party dependencies.

---

## 🔒 Governance & Safety
- **[Tool Approval & Allowlisting](governance/tool-approval.md)**: Enforce rule-based permission policies and human-in-the-loop approval gates.
- **[Circuit Breakers & Observability](governance/circuit-breakers.md)**: Protect against cascade failures, monitor tool telemetry, and emit OpenTelemetry metrics.

---

## 👥 Multi-Agent Systems & Testing
- **[Subagents & Teams](agents/multi-agent-and-subagents.md)**: Orchestrate hierarchical subagent dispatching, group chats, and multi-model cost routing.
- **[Unit Testing Agents](testing/testing-agents.md)**: Write fast, deterministic unit tests using `MyAgents.Testing` and in-memory agents.
