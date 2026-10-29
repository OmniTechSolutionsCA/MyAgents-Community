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
- **`MyAgents.Generators` Roslyn incremental source generator for `[AgentTool]`.** Generates an `ITool`
  implementation per annotated method, with the JSON schema emitted as a compile-time constant and
  `As{Method}Tool()` / `AsAgentTools()` registration helpers. Primitive arguments (`string`, `bool`, `int`,
  `long`, `float`, `double`, `decimal`) are bound without reflection or boxing. Generated tools follow the
  same argument contract as `DelegateTool` (case-insensitive names, `ArgumentException` for missing required
  arguments, `JsonException` for wrongly typed values, invariant-culture results), enforced by
  `ToolArgumentBindingContractTests`.
- **NativeAOT-safe `[AgentTool]` authoring via `JsonContext`.** `AgentToolAttribute` gains a `JsonContext`
  property pointing at a `JsonSerializerContext`. Generated tools then bind complex arguments and serialize
  results through source-generated `JsonTypeInfo` metadata (`JsonSerializerContext.GetTypeInfo`), and advertise
  exporter-accurate JSON schemas for complex parameters (nested properties, unions, defaults, required fields)
  derived from that same metadata at first use — no reflection, NativeAOT/trimming safe. Missing
  `[JsonSerializable]` registrations are reported as compile-time errors (MYAG001); complex types without a
  context fall back to reflection-based serialization and produce a warning (MYAG002). Primitive binding,
  argument contracts, and the constant-schema path for primitive-only tools are unchanged.
- **`community/samples/NativeAotTools`.** Publishes with `PublishAot`/`IsAotCompatible` and runs the generated
  tools as a standalone native executable (verified: `dotnet publish -c Release -r win-x64`, executed with exit
  code 0 — schema advertisement, complex argument binding, complex async results, and missing/mistyped
  argument rejection all pass without a JIT).
- **`SourceGeneratedJsonContextToolTests`.** Deterministic contract coverage for the `JsonContext` path:
  exporter-accurate schemas, complex/nullable/collection parameters, complex async results, cancellation,
  malformed and missing arguments, and unchanged primitive-tool behavior.
- **`eng/MyAgents.Benchmarks` BenchmarkDotNet suite.** 8 benchmark classes (21 benchmarks) with
  `[MemoryDiagnoser]` covering `DelegateTool`, `BM25Index`, `VectorMemoryStore`, `HybridMemoryRetriever`,
  `ChatClientRuntime`, `AgentPipeline`, `AgentGraph`, and `SqliteCheckpointer`.
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
  now happens before any output is released, at latency parity with the blocking path. This is a
  deliberate trade: text already handed to a caller cannot be retracted, and `IGuardrailPolicy`
  cannot be evaluated per-delta. Middleware that wants true incremental streaming overrides
  `InvokeStreamingAsync`; the guardrail middleware deliberately does not.
  `ChatClientRuntime.InvokeStreamingAsync` itself still emits incremental updates, so streaming
  remains token-level for agents without guardrails.
- **Code analysis tightened.** Enabled `<AnalysisMode>Recommended</AnalysisMode>` across all packages
  with zero warnings and zero errors.

### Performance
Figures below are BenchmarkDotNet default-job means, `main` vs this change, measured on one machine
(AMD Ryzen AI 9 HX 370, .NET 10.0.14) pinned to a single logical CPU.

- **Native `TensorPrimitives` `VectorMemoryStore`.** Replaced the preview `Microsoft.SemanticKernel.Connectors.InMemory`
  dependency with an in-memory store using `TensorPrimitives.CosineSimilarity` and a bounded min-heap.
  Search over 5,000 × 1,536-dim vectors: 698.8 µs → 478.6 µs (1.46x), 330.1 KB → 41.7 KB allocated.
  Upsert with metadata: 298.8 ns → 69.9 ns (4.3x), 856 B → 312 B.
- **`HybridMemoryRetriever` single-pass fusion.** One candidate dictionary via `CollectionsMarshal.GetValueRefOrAddDefault`
  and a bounded heap instead of LINQ rank maps and sorts. RRF top-10: 1.214 ms → 177.7 µs (6.8x),
  782 KB → 53.7 KB allocated; weighted linear top-10: 1.235 ms → 181.2 µs (6.8x).
- **`DelegateTool` compiled direct parameter binding.** Removes the per-invocation `object?[]` and boxing for
  primitive arguments. Primitive invocation: 379.5 ns → 186.7 ns (2.0x), 280 B → 72 B. `ValueTask<T>` invocation:
  439.1 ns → 372.0 ns, 400 B → 296 B. The typed invoker is compiled lazily on first invocation, so
  `DelegateTool.Create` drops from 178.6 µs to 1.1 µs; the first call to each tool pays ~3 ms for compilation.
  Source-generated `[AgentTool]` tools have no compilation step.
- **`BM25Index` array-indexed scoring.** Pooled `ArrayPool<double>` score accumulators, struct `Posting` lists, a
  bounded min-heap for top-k, and `string.Create` lowercasing. Short query over 10,000 documents: 1,533.7 µs → 47.5 µs
  (32x), 921 KB → 824 B allocated; long query: 3,833.6 µs → 129.7 µs (30x). Single-document `AddOrUpdate`:
  1.76 µs → 1.40 µs, 4.69 KB → 3.5 KB.
- **`SqliteCheckpointer` prepared command caching.** Save and load reuse prepared `SqliteCommand` instances with
  pre-bound parameters under the existing gate. Save + load: 47.9 µs → 43.9 µs, 15.48 KB → 13.3 KB.
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
