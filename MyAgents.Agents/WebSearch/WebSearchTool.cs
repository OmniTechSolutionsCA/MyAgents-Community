using System.Text.Json;
using MyAgents.Core.Tools;

namespace MyAgents.Agents.WebSearch;

/// <summary>
/// Tool that performs web searches and returns formatted results.
/// </summary>
public sealed class WebSearchTool : ITool
{
    private readonly IWebSearchProvider _provider;

    public string Name => "web_search";
    public string? Description => "Searches the web for current information. Use this when you need up-to-date facts, current events, or information not in your training data.";

    public WebSearchTool(IWebSearchProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public async ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        try
        {
            // Parse arguments
            var args = JsonSerializer.Deserialize<WebSearchArgs>(jsonArguments);
            var query = args?.Query;

            if (string.IsNullOrWhiteSpace(query))
            {
                return JsonSerializer.Serialize(new { error = "Query parameter is required." });
            }

            // Perform search
            var results = await _provider.SearchAsync(query, cancellationToken);

            // Format results
            var formatted = new
            {
                query,
                results = results.Select(r => new
                {
                    title = r.Title,
                    url = r.Url,
                    snippet = r.Snippet
                }).ToList()
            };

            return JsonSerializer.Serialize(formatted);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Search failed: {ex.Message}"
            });
        }
    }

    private sealed class WebSearchArgs
    {
        public string? Query { get; set; }
    }
}
