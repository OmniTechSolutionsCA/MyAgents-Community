using MyAgents.Core;
using MyAgents.Core.Tools;

namespace MyAgents.Agents.Scrapai;

/// <summary>
/// Agent feature that supplies the ScrapAI CLI tool and a guiding system prompt.
/// </summary>
public sealed class ScrapaiFeature : IAgentFeature
{
    private readonly IProcessRunner _runner;
    private readonly string _executablePath;

    /// <summary>
    /// Creates a new feature instance.
    /// </summary>
    /// <param name="runner">Process runner used to invoke the executable.</param>
    /// <param name="executablePath">
    /// Path or name of the scrapai executable (defaults to "scrapai").
    /// </param>
    public ScrapaiFeature(IProcessRunner runner, string executablePath = "scrapai")
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _executablePath = executablePath;
    }

    public void Contribute(AgentAssembly assembly)
    {
        assembly.Tools.Add(new ScrapaiTool(_runner, _executablePath));
        assembly.Tools.Add(new ScrapaiListSpidersTool(_runner, _executablePath));
        assembly.Tools.Add(new ScrapaiCrawlTool(_runner, _executablePath));
        assembly.Prompts.AddSystem(
            "You have access to the ScrapAI CLI tool. " +
            "Prefer typed tools for common operations: 'scrapai_list_spiders' with { project }, and 'scrapai_crawl' with { project, spider, limit? }. " +
            "Use the generic 'scrapai' function with { Args: [...] } only for commands that do not have a dedicated helper. " +
            "All ScrapAI tools return a JSON blob containing exitCode, stdout, stderr, and error. " +
            "Do not attempt to execute arbitrary shell commands."
        );
    }
}
