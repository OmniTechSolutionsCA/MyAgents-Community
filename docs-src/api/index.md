# C# API Reference

Browse the complete public API reference generated directly from the MyAgents Community v1.0.0 assemblies and XML documentation comments.

## Namespaces by Package

| Namespace | Package | Description |
| :--- | :--- | :--- |
| **`MyAgents.Core`** | `MyAgents.Core` | Core agent, session, message, feature, approval, middleware, and telemetry contracts. |
| **`MyAgents.Core.Execution`** | `MyAgents.Core` | Durable run requests, leases, fencing tokens, batch catalogs, and `IAgentRunBackend`. |
| **`MyAgents.Core.Memory`** | `MyAgents.Core` | Core `IMemoryStore`, `MemoryEntry`, and `IMemoryStoreChangeNotifier` primitives. |
| **`MyAgents.Core.Messaging`** | `MyAgents.Core` | `IAgentChannel` and `InMemoryAgentChannel` pub/sub envelopes. |
| **`MyAgents.Core.Prompts`** | `MyAgents.Core` | Versioned `PromptRegistry` and `PromptTemplateDefinition`. |
| **`MyAgents.Core.Tools`** | `MyAgents.Core` | `ITool`, `DelegateTool`, `[AgentTool]`, `IToolCallObserver`, and `IProcessRunner`. |
| **`MyAgents.Core.Workflows`** | `MyAgents.Core` | `AgentGraph<TState>`, `WorkflowHost`, `IWorkflow<TState>`, `AgentLoop`, `ReflectionSteps`, `OrchestratorSteps`, `ConsensusSteps`, and checkpointers. |
| **`MyAgents.Agents`** | `MyAgents.Agents` | Built-in features (`ToolsFeature`, `GuardrailsFeature`, `StructuredOutputFeature<T>`, `FallbackFeature`), `GroupChat`, subagents, and multi-model cost routing. |
| **`MyAgents.Agents.WebSearch`** | `MyAgents.Agents` | `IWebSearchProvider`, `BraveSearchProvider`, and `WebSearchFeature`. |
| **`MyAgents.Agents.Scrapai`** | `MyAgents.Agents` | `ScrapaiFeature` and spider execution tools. |
| **`MyAgents.Runtime.Chat`** | `MyAgents.Runtime.Chat` | `ChatClientRuntime`, `ChatClientRuntimeOptions`, and `AddMyAgentsChatRuntime`. |
| **`MyAgents.Runtime.Workers`** | `MyAgents.Runtime.Workers` | `AgentRunWorkerService`, batch submitters/reporters/controllers, and `WorkflowAgentRunHandler<TState>`. |
| **`MyAgents.Runtime.Workers.Sqlite`** | `MyAgents.Runtime.Workers.Sqlite` | `SqliteAgentRunBackend` for transactional single-node durable runs. |
| **`MyAgents.Memory`** | `MyAgents.Memory` | `HybridMemoryRetriever`, `BM25Index`, `SqliteMemoryStore`, `VectorMemoryStore`, `VectorDataMemoryStore`, `DefaultDocumentIngestor`, and `EpisodicMemoryStore`. |
| **`MyAgents.Mcp`** | `MyAgents.Mcp` | `McpFeature`, `McpMultiServerFeature`, `McpHotReloadFeature`, `DynamicToolCatalog`, and `McpSkillDiscoveryService`. |
| **`MyAgents.Testing`** | `MyAgents.Testing` | `InMemoryAgent`, `FakeAgentSession`, `InMemoryAgentRuntime`, and `InMemoryAgentRunBackend`. |
| **`MyAgents.Generators`** | `MyAgents.Generators` | `AgentToolGenerator` Roslyn incremental source generator. |
