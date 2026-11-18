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
- **[Structured Output & Multi-Modal](core/structured-output-and-multimodal.md)**: Enforce JSON schemas with automatic self-correction retries and send image, audio, video, or document inputs.
- **[Prompt Registry & Templates](core/prompt-registry.md)**: Manage versioned system prompt templates with variable interpolation.

---

## ⚡ Tools & Extensibility
- **[NativeAOT Tools](tools/nativeaot-tools.md)**: Generate zero-reflection, compile-time tools using `MyAgents.Generators`.
- **[Delegate & Custom Tools](tools/delegate-tools.md)**: Create tools from in-process C# lambdas, `ToolsFeature`, `WebSearchFeature`, or custom `ITool` classes.
- **[Model Context Protocol (MCP)](tools/mcp.md)**: Connect your agent to external tools, prompts, and resources over stdio or HTTP/SSE with dynamic hot-reload and skill discovery.

---

## 🔀 Workflows & State Graphs
- **[Agent State Graphs](workflows/agent-graphs.md)**: Build cyclic or DAG workflows with `AgentGraph<TState>`, conditional routing, parallel fan-out, `SendPacket<TState>` map-reduce, and snapshot time-travel.
- **[Checkpointed Workflows & HITL](workflows/checkpointed-workflows.md)**: Pause and resume stateful `IWorkflow<TState>` pipelines across process restarts for human input or tool approvals.
- **[Orchestration Patterns](workflows/orchestration-patterns.md)**: Execute iterative `AgentLoop` reductions, planner-worker-synthesizer plans (`OrchestratorSteps`), generator-critic loops (`ReflectionSteps`), majority-vote `ConsensusSteps`, and adaptive goal prioritization.

---

## 🛡️ Durable Execution & Resilience
- **[Durable Agent Runs & Batches](execution/durable-runs.md)**: Host crash-resilient, restart-recoverable workloads using SQLite leases, admission throttling, batch controllers, and background worker services.

---

## 🔍 Memory & Retrieval-Augmented Generation (RAG)
- **[Hybrid RAG & Vector Stores](memory/hybrid-rag.md)**: Fuse dense SIMD cosine similarity (`TensorPrimitives.Dot`) with BM25 sparse lexical ranking across in-memory, SQLite, and `Microsoft.Extensions.VectorData` stores.
- **[Document Ingestion & Chunking](memory/document-ingestion.md)**: Ingest and chunk Markdown, HTML, Plain Text, and JSON with zero third-party dependencies, content-hash deduplication, and agentic knowledge base tools.
- **[Episodic Memory](memory/episodic-memory.md)**: Enable cross-session recall, automated episode consolidation, and retention policies.

---

## 🔒 Governance, Guardrails & Observability
- **[Tool Approval & Allowlisting](governance/tool-approval.md)**: Enforce rule-based permission policies, session approval memory, audit sinks, and human-in-the-loop approval gates.
- **[Guardrails, Fallbacks & Resilience](governance/guardrails-and-resilience.md)**: Protect agent outputs with `GuardrailsFeature`, retry transient provider errors with `ResilienceMiddleware`, and capture failures with `FallbackFeature` and dead-letter queues.
- **[Circuit Breakers & Observability](governance/circuit-breakers.md)**: Protect against cascade failures, monitor tool invocations with `IToolCallObserver`, and emit OpenTelemetry traces and metrics (`AddMyAgentsTracing`, `AddMyAgentsMetrics`).

---

## 👥 Multi-Agent Systems & Testing
- **[Subagents, Group Chats & Routing](agents/multi-agent-and-subagents.md)**: Orchestrate hierarchical subagent dispatching, `GroupChat` / `SelectorGroupChat`, `CapabilityRouter`, and multi-model token cost routing.
- **[Unit Testing Agents](testing/testing-agents.md)**: Write fast, deterministic unit tests using `MyAgents.Testing` (`InMemoryAgent`, `FakeAgentSession`, `InMemoryAgentRuntime`, `InMemoryAgentRunBackend`).
