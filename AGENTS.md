# AGENTS.md

Guidance for AI coding agents and contributors working on MyAgents Community.

## What this is

MyAgents Community is a .NET 10 agent orchestration framework built on `Microsoft.Extensions.AI`. This repository is the MIT-licensed Community distribution; proprietary modules and private exports belong in `MyAgents` and must not be added here. See [README.md](README.md) for the architecture overview and [CONTRIBUTING.md](CONTRIBUTING.md) for contributor guidelines.

## Code organization & file guidelines

Use one independently meaningful top-level type per file. The file name must match its primary type. Small nested or tightly coupled supporting types may remain in the same file when they have no independent lifecycle, reuse, or testing requirements. Organize files by domain capability and use case while preserving architectural layer boundaries.

## Project layout

- `MyAgents.Core` — Core contracts (`IAgent`, `IAgentRuntime`, `IAgentSession`, `IAgentFeature`, `ITool`, `IWorkflow<T>`, middleware, and assembly definitions) with zero provider or external runtime coupling.
- `MyAgents.Runtime.Chat` — In-process `IChatClient` turn execution, tool loop, streaming, checkpointer, and multi-model routing.
- `MyAgents.Agents` — Built-in agent features, guardrails, observability, and web search capabilities.
- `MyAgents.Memory` — Memory abstractions, hybrid retrieval, and vector/in-memory stores.
- `MyAgents.Mcp` — Model Context Protocol (`.NET` MCP) tool, prompt, and resource adapters.
- `MyAgents.Testing` — Deterministic test doubles and assertions for testing agents without live model providers.
- `community/tests/MyAgents.Community.Tests` — NUnit test suite for the Community distribution.
- `community/samples/ChatWithTools` — Sample application demonstrating agent composition and tool calling.

## Build, test & pack

```powershell
dotnet restore MyAgents.Community.slnx
dotnet build MyAgents.Community.slnx --no-restore -c Release
dotnet test MyAgents.Community.slnx --no-build -c Release
dotnet pack MyAgents.Community.slnx --no-build --no-restore -c Release -o artifacts/packages
```

## Key conventions

- **Warnings are errors.** `TreatWarningsAsErrors` and `EnforceCodeStyleInBuild` are enabled across the solution in `Directory.Build.props`.
- **Preserve layer boundaries.** `MyAgents.Core` must remain provider-neutral and lightweight; concrete runtime, protocol, or feature implementations belong in their respective packages.
- **Test deterministically.** Add or update NUnit tests in `community/tests/MyAgents.Community.Tests` using `MyAgents.Testing` doubles rather than live external services.
