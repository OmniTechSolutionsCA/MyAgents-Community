# Changelog

All notable changes to MyAgents Community are documented here. This project
follows the Keep a Changelog format and Semantic Versioning after its first
stable release.

## [Unreleased]

### Fixed
- **Streaming content loss with blocking middleware.** `IAgentMiddleware`'s default
  streaming bridge accumulated the chunks of a turn and then yielded only the final one,
  so registering any middleware that implements just the blocking path (for example
  `LoggingFeature`) silently truncated the caller's response to its last delta. The bridge
  now folds the chunks into the shape a blocking turn would have produced and yields the
  complete turn.
- **Guardrails could be bypassed on the streaming path.** `GuardrailMiddleware` evaluated
  only the last delta of a stream and then appended its replacement message to text that had
  already been delivered — leaking the flagged content while appearing to block it. Guardrails
  now buffer the whole turn, evaluate policies against it, and release either the clean turn
  or only the replacement message.
- **Streamed fragments were treated as separate messages.** Because `IGuardrailPolicy` is a
  whole-turn contract, per-delta messages defeated it: the blocklist policy joins messages with
  a newline, which severed a blocked phrase spanning two deltas, and `MaxLengthGuardrailPolicy`
  summed per-message lengths instead of the turn total.

### Added
- `AgentOutputAccumulator`: folds the chunks of a streamed turn into the single result a blocking
  turn would have produced. Exposed publicly because any middleware overriding
  `IAgentMiddleware.InvokeStreamingAsync` needs the same fold to reason about a complete turn.
- `PublicApiApprovalTests` and approved public-API baselines, so that an intentional or accidental
  change to any package's public surface fails the build instead of shipping silently.

### Changed
- **An agent using `GuardrailsFeature` no longer streams token-by-token.** Guardrail evaluation
  now happens before any output is released, at latency parity with the blocking path. This is a
  deliberate trade: text already handed to a caller cannot be retracted, and `IGuardrailPolicy`
  cannot be evaluated per-delta. Middleware that wants true incremental streaming overrides
  `InvokeStreamingAsync`; the guardrail middleware deliberately does not.
  `ChatClientRuntime.InvokeStreamingAsync` itself still emits incremental updates, so streaming
  remains token-level for agents without guardrails.

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
