namespace MyAgents.Agents.WebSearch;

/// <summary>
/// Represents a web search result.
/// </summary>
/// <param name="Title">The title of the result.</param>
/// <param name="Url">The URL of the result.</param>
/// <param name="Snippet">A text snippet from the result.</param>
public sealed record SearchResult(
    string Title,
    string Url,
    string Snippet);

/// <summary>
/// Provider interface for web search functionality.
/// Implementations can use different search APIs (Brave, Bing, SerpAPI, etc.).
/// </summary>
public interface IWebSearchProvider
{
    /// <summary>
    /// Performs a web search with the given query.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Search results.</returns>
    Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default);
}
