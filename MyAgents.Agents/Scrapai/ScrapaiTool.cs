using System.Text.Json;
using MyAgents.Core.Tools;

namespace MyAgents.Agents.Scrapai;

/// <summary>
/// Tool that invokes the ScrapAI command‑line interface. Arguments are passed as
/// a JSON object containing an <c>Args</c> array of strings.
/// </summary>
public sealed class ScrapaiTool : ScrapaiToolBase
{
    public override string Name => "scrapai";

    public override string? Description => "Runs the ScrapAI CLI; arguments should be supplied as a JSON object with an 'Args' array.";

    public ScrapaiTool(IProcessRunner runner, string executablePath = "scrapai")
        : base(runner, executablePath)
    {
    }

    public override ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        if (!TryDeserialize<ScrapaiArgs>(jsonArguments, out var args, out var response))
        {
            return ValueTask.FromResult(response);
        }

        if (args?.Args == null || args.Args.Length == 0)
        {
            return ValueTask.FromResult(SerializeValidationError("'Args' array is required"));
        }

        return InvokeProcessAsync(args.Args, cancellationToken);
    }

    private sealed class ScrapaiArgs
    {
        public string[]? Args { get; set; }
    }
}
