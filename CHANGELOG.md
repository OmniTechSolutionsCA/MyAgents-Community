# Changelog

All notable changes to MyAgents Community are documented here. This project
follows the Keep a Changelog format and Semantic Versioning after its first
stable release.

## [Unreleased]

## [0.2.0-preview.1] - 2026-10-27

### Added
- **Strongly-Typed Structured Outputs**:
  - `AgentResponseFormat`: Declarative response formats (`Text`, `Json`, `ForJsonSchema`, `ForType<T>`) powered by .NET 10 `JsonSchemaExporter`.
  - `AgentSessionStructuredExtensions.InvokeAsync<T>`: Directly invoke turns constrained to strongly-typed schema structures with markdown fence stripping and automatic deserialization.
  - `StructuredOutputFeature<T>` and `StructuredOutputMiddleware<T>`: Turn pipeline middleware with self-correction retry feedback when models produce invalid JSON.
  - `AgentCapabilities.StructuredOutput`: Well-known capability constant.
- **Ergonomic Tool Authoring**:
  - `DelegateTool`: Wraps any C# method, lambda, or local function as an `ITool` with automatic JSON schema generation from parameter types and `[Description]` attributes, parameter binding, default value resolution, nullability checks, and `CancellationToken` injection.
  - `ToolCatalogBuilder.AddFunction`: Fluent method for registering delegate tools.
  - `ToolsFeature`: Reusable agent feature for registering `ITool` instances or delegate functions directly on agent definitions.
- **True Streaming & Parallel Tool Execution**:
  - `ChatClientRuntime.InvokeStreamingAsync`: Emits incremental token-by-token response updates in real time via `IChatClient.GetStreamingResponseAsync`, preserving multi-round tool execution loops.
  - `ChatClientRuntimeOptions.MaxParallelToolCalls`: Allows concurrent execution of multiple tool calls emitted within a single model turn via `Task.WhenAll`.
  - Forwarding of `AgentResponseFormat` to `ChatOptions.ResponseFormat` (`ChatResponseFormat.ForJsonSchema`).

## [0.1.0-preview.1] - 2026-10-27

### Added
- Initial Community distribution candidate with provider-neutral chat and tool
  execution, memory, MCP integration, and deterministic testing.
- An Ollama and OpenAI-compatible chat and clock-tool sample with an explicit
  tool allowlist.
- Tests for denied and unknown tools, cancellation, tool-call limits, and tool
  failure observations.

### Security
- Tool calls are allowed when no approval policy is registered; applications
  should configure an explicit policy before exposing sensitive tools.
- Keep SQLitePCLRaw at 2.1.13 for Community builds so NuGet cannot resolve the
  vulnerable 2.1.11 native SQLite dependency (GHSA-2m69-gcr7-jv3q).
