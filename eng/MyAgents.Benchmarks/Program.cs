using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Running;

namespace MyAgents.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        // Uses BenchmarkDotNet's default job for publishable numbers. Pass `--job short`
        // for a quick local signal; ShortRun results are too noisy to quote.
        var config = DefaultConfig.Instance
            .AddDiagnoser(MemoryDiagnoser.Default);

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }
}
