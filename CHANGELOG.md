# Changelog

All notable changes to MyAgents Community are documented here. This project
follows the Keep a Changelog format and Semantic Versioning after its first
stable release.

## [Unreleased]

### Fixed
- **Streaming content loss with blocking middleware.** `IAgentMiddleware`'s default
  streaming bridge accumulated the chunks of a turn and then yielded only the final one,
  so registering any middleware that implements just the blocking path silently truncated
  the caller's response to its last delta. The bridge now folds the chunks into the shape a
  blocking turn would have produced and yields the complete turn.
- **Guardrails could be bypassed on the streaming path.** `GuardrailMiddleware` evaluated
  only the last delta of a stream and then appended its replacement message to text that had
  already been delivered — leaking the flagged content while appearing to block it. Guardrails
  now buffer the whole turn, evaluate policies against it, and release either the clean turn
  or only the replacement message.
- **Streamed fragments were treated as separate messages.** Because `IGuardrailPolicy` is a
  whole-turn contract, per-delta messages defeated it: the blocklist policy joins messages with
  a newline, which severed a blocked phrase spanning two deltas, and `MaxLengthGuardrailPolicy`
  summed per-message lengths instead of the turn total.
- **Cancellation ignored in process execution and session creation.** `DefaultProcessRunner`
  failed to check cancellation before launching processes, dropped tokens in output stream readers,
  and failed to throw `OperationCanceledException` on pre-cancelled tokens. `DefaultAgentSessionFactory.CreateSessionAsync`
  did not forward the `cancellationToken` to feature `ContributeAsync` calls.

### Added
- **`MyAgents.Generators` Roslyn incremental source generator for `[AgentTool]`.** Introduced compile-time source
  generation for agent tools, emitting 100% reflection-free `ITool` wrappers with static precomputed JSON schema constants
  and zero-boxing argument extractors. Completely eliminates runtime expression compilation and schema generation overhead
  (saving 5.18 ms and 12.5 KB per tool registration) while ensuring full NativeAOT compatibility.
- **`eng/MyAgents.Benchmarks` BenchmarkDotNet suite.** Added automated benchmark harness with 7 suites
  measuring execution latency and heap allocations (`[MemoryDiagnoser]`) across `DelegateTool`, `BM25Index`,
  `VectorMemoryStore`, `HybridMemoryRetriever`, `ChatClientRuntime`, `AgentPipeline`, `AgentGraph`, and `SqliteCheckpointer`.
- `AgentOutputAccumulator`: folds the chunks of a streamed turn into the single result a blocking
  turn would have produced. Exposed publicly because any middleware overriding
  `IAgentMiddleware.InvokeStreamingAsync` needs the same fold to reason about a complete turn.
- `PublicApiApprovalTests` and approved public-API baselines across all six packages, ensuring that
  unintended changes to public APIs fail the build immediately.
- Contract test suites: `StreamingPipelineTests`, `ProcessRunnerTests`, `SessionCreationTests`.

### Changed
- **`LoggingMiddleware` streaming is now natively incremental.** Upgraded `LoggingMiddleware.InvokeStreamingAsync`
  to pass through token deltas in real-time while recording duration and tool calls, preserving incremental
  streaming for callers using `LoggingFeature`.
- **An agent using `GuardrailsFeature` no longer streams token-by-token.** Guardrail evaluation
  now happens before any output is released, at parity with the blocking path. This is a
  deliberate trade: text already handed to a caller cannot be retracted, and `IGuardrailPolicy`
  cannot be evaluated per-delta. Middleware that wants true incremental streaming overrides
  `InvokeStreamingAsync`; the guardrail middleware deliberately does not.
  `ChatClientRuntime.InvokeStreamingAsync` itself still emits incremental updates, so streaming
  remains token-level for agents without guardrails.
- **Code analysis tightened.** Enabled `<AnalysisMode>Recommended</AnalysisMode>` across all packages
  with zero warnings and zero errors.

### Performance
- **Native SIMD `TensorPrimitives` `VectorMemoryStore`.** Replaced preview `Microsoft.SemanticKernel.Connectors.InMemory`
  with a zero-allocation, SIMD-accelerated vector store using `TensorPrimitives.CosineSimilarity` and bounded min-heap priority queues.
  For 5,000 vector records, search allocations plummeted from 322.96 KB to **41.68 KB (87% allocation reduction)** and execution latency
  dropped from 687 µs to **479 µs (1.43x faster)**. Single upsert latency dropped from 203.5 ns to **72.0 ns (2.8x faster)** with allocations
  reduced by 58% (312 B vs 752 B). Hybrid retrieval latency was cut in half from 408 µs to **200 µs (2.04x faster)** and allocations were cut
  by 6.1x (53.58 KB vs 328.6 KB).
- **Compile-time `[AgentTool]` zero cold-start invocation.** Replaced runtime `DelegateTool.Create` expression compilation (5.18 ms and 12.5 KB)
  with compile-time Roslyn source-generated `ITool` implementations, achieving instant 0 ns startup, static schema constant emission, and
  allocation-free 72 B invocation matching handwritten `ITool`.
- **`DelegateTool` compiled direct parameter binding.** Eliminated the per-invocation `object?[]` array
  allocation and boxing for primitive arguments (`int`, `long`, `double`, `float`, `bool`, `string`, `CancellationToken`).
  Invocation latency dropped from 386.5 ns to 196.9 ns (1.96x faster) and allocated memory dropped from 280 B to 72 B (74% reduction),
  matching the performance of handwritten `ITool` implementations.
- **`BM25Index` array-indexed scoring & zero-allocation tokenization.** Replaced dynamic dictionary resizing and rehashing
  with rented `ArrayPool<double>` score accumulators, contiguous struct `Posting` lists, and `string.Create` lowercasing.
  For 10,000 indexed documents, search latency dropped from 1.55 ms to 47.47 μs (32.7x faster) and allocations dropped from
  920.98 KB to 816 B (1,128x reduction), completely eliminating Gen1/Gen2 garbage collection pauses.
- **`SqliteCheckpointer` prepared command caching.** Cached pre-allocated and prepared `SqliteCommand` statements and
  reusable parameter bindings (`_saveCommand`, `_loadCommand`, `_deleteCommand`), eliminating per-checkpoint command creation,
  SQL re-parsing, and `SqliteParameter` allocations.
- **Zero-allocation logging.** Replaced runtime reflection / dynamic logging across `ResilienceMiddleware`,
  `FallbackMiddleware`, `CircuitBreakerMiddleware`, `ContextWindowMiddleware`, `CapabilityRouter`, and
  `LoggingFeature` with source-generated `[LoggerMessage]` partial methods.
- **Chat client runtime hot-loop optimization.** Eliminated LINQ and empty dictionary allocations during
  function call extraction in `ChatClientRuntime`.
- **Internal collection devirtualization.** Devirtualized private/internal collections across Core, Agents,
  Memory, Mcp, and Chat runtime to concrete types.

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
