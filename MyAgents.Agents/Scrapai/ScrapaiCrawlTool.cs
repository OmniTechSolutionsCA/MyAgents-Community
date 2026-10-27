using System.Globalization;
using MyAgents.Core.Tools;

namespace MyAgents.Agents.Scrapai;

/// <summary>
/// Crawls a named ScrapAI spider inside a project.
/// </summary>
public sealed class ScrapaiCrawlTool : ScrapaiToolBase
{
    public ScrapaiCrawlTool(IProcessRunner runner, string executablePath = "scrapai")
        : base(runner, executablePath)
    {
    }

    public override string Name => "scrapai_crawl";

    public override string? Description => "Runs a ScrapAI spider. Arguments: { \"project\": \"name\", \"spider\": \"spiderName\", \"limit\": 10 }. 'limit' is optional.";

    public override ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        if (!TryDeserialize<CrawlArgs>(jsonArguments, out var args, out var response))
        {
            return ValueTask.FromResult(response);
        }

        if (string.IsNullOrWhiteSpace(args?.Project))
        {
            return ValueTask.FromResult(SerializeValidationError("'project' is required"));
        }

        if (string.IsNullOrWhiteSpace(args.Spider))
        {
            return ValueTask.FromResult(SerializeValidationError("'spider' is required"));
        }

        if (args.Limit is <= 0)
        {
            return ValueTask.FromResult(SerializeValidationError("'limit' must be greater than zero when provided"));
        }

        var commandArgs = new List<string> { "crawl", args.Spider, "--project", args.Project };
        if (args.Limit.HasValue)
        {
            commandArgs.Add("--limit");
            commandArgs.Add(args.Limit.Value.ToString(CultureInfo.InvariantCulture));
        }

        return InvokeProcessAsync(commandArgs, cancellationToken);
    }

    private sealed class CrawlArgs
    {
        public string? Project { get; set; }

        public string? Spider { get; set; }

        public int? Limit { get; set; }
    }
}