using MyAgents.Core.Tools;

namespace MyAgents.Agents.Scrapai;

/// <summary>
/// Lists spiders for a given ScrapAI project.
/// </summary>
public sealed class ScrapaiListSpidersTool : ScrapaiToolBase
{
    public ScrapaiListSpidersTool(IProcessRunner runner, string executablePath = "scrapai")
        : base(runner, executablePath)
    {
    }

    public override string Name => "scrapai_list_spiders";

    public override string? Description => "Lists ScrapAI spiders for a project. Arguments: { \"project\": \"name\" }.";

    public override ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        if (!TryDeserialize<ListSpidersArgs>(jsonArguments, out var args, out var response))
        {
            return ValueTask.FromResult(response);
        }

        if (string.IsNullOrWhiteSpace(args?.Project))
        {
            return ValueTask.FromResult(SerializeValidationError("'project' is required"));
        }

        return InvokeProcessAsync(["spiders", "list", "--project", args.Project], cancellationToken);
    }

    private sealed class ListSpidersArgs
    {
        public string? Project { get; set; }
    }
}