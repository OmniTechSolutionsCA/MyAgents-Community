using MyAgents.Core;

namespace MyAgents.Agents.WebSearch;

/// <summary>
/// Feature that adds web search capability to an agent.
/// </summary>
public sealed class WebSearchFeature : IAgentFeature
{
    private readonly IWebSearchProvider _provider;

    public WebSearchFeature(IWebSearchProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public void Contribute(AgentAssembly assembly)
    {
        assembly.Tools.Add(new WebSearchTool(_provider));
        assembly.Prompts.AddSystem(
            "You have access to a web search tool. Use it when the user asks about current events, " +
            "recent information, or topics that may have changed since your training data. " +
            "Always cite your sources when using web search results.");
    }
}
