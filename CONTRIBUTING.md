# Contributing to MyAgents Community

Thanks for considering a contribution. This repository is the Community
distribution only; proprietary projects and generated private-source exports
must not be added.

## Before opening a pull request

1. Open an issue first for larger changes so the scope can be agreed.
2. Keep changes focused and include tests for behavior changes.
3. Update public API documentation and `CHANGELOG.md` when appropriate.
4. Do not include credentials, customer data, proprietary source, generated
   build output, or dependencies whose redistribution rights are unclear.
5. If making changes to hot execution paths, run the relevant benchmarks to verify
   no regressions in latency or allocations.

## Local validation

Install the .NET 10 SDK and PowerShell 7, then run these commands from the
repository root:

```powershell
dotnet restore MyAgents.Community.slnx
dotnet build MyAgents.Community.slnx --no-restore -c Release
dotnet test MyAgents.Community.slnx --no-build -c Release
dotnet pack MyAgents.Community.slnx --no-build --no-restore -c Release -o artifacts/packages
```

Run these checks locally before submitting a pull request. The Community
validation workflow is also configured to run against the sanitized export on
Windows, Linux, and macOS when GitHub Actions is enabled.

## Running Benchmarks

MyAgents includes an automated BenchmarkDotNet test suite located under `eng/MyAgents.Benchmarks/`.
It measures both execution latency and heap allocations (`[MemoryDiagnoser]`) across hot paths
(tool invocations, BM25 indexing/search, vector/hybrid memory retrievals, agent pipeline dispatch,
chat runtime, and workflows) without external network or LLM variability.

### Running specific benchmarks

```powershell
# Run BM25 index & ranking benchmarks
dotnet run --project eng/MyAgents.Benchmarks/MyAgents.Benchmarks.csproj -c Release -- --filter *BM25IndexBenchmarks*

# Run DelegateTool invocation & binding benchmarks
dotnet run --project eng/MyAgents.Benchmarks/MyAgents.Benchmarks.csproj -c Release -- --filter *DelegateToolBenchmarks*

# Run AgentPipeline & ChatClientRuntime benchmarks
dotnet run --project eng/MyAgents.Benchmarks/MyAgents.Benchmarks.csproj -c Release -- --filter *Runtime*

# Run all benchmarks
dotnet run --project eng/MyAgents.Benchmarks/MyAgents.Benchmarks.csproj -c Release -- --filter *
```

The harness uses BenchmarkDotNet's default job. Append `--job short` for a quick local signal, but
do not quote ShortRun numbers: with three iterations the error is often as large as the mean. On
hybrid CPUs (performance + efficiency cores), pin to one logical CPU with `--affinity <mask>`, run the
baseline and the change back-to-back with the same mask, and check that the `HandwrittenITool_PrimitiveArgs`
control matches on both sides before comparing timings.

### Performance Budgets

When contributing to core dispatch loops or memory indexing paths, maintain these targets
(measured on an AMD Ryzen AI 9 HX 370 pinned to one logical CPU; compare against `main` on your own machine):
- **`DelegateTool.InvokeAsync` (primitives)**: ≤ 200 ns and ≤ 72 B allocated (zero boxing, compiled expression binding).
- **`BM25Index.Search` (10k docs, Top-10)**: ≤ 50 μs and < 1 KB allocated (pooled accumulator, min-heap ranking).
- **`AgentPipeline.InvokeAsync` (cached chain)**: ≤ 15 ns and 0 B allocated.

## Pull requests

Use the pull request template. Describe the user-visible change, compatibility
impact, and validation performed. Keep commits reviewable, and do not merge or
publish a release from a pull request.

By submitting a contribution, you confirm that you have the right to submit it
under the repository's MIT license. No separate contributor agreement is
required at this time.
