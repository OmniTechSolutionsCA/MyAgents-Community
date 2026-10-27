using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyAgents.Agents.WebSearch;

/// <summary>
/// Brave Search provider using the Brave Search LLM Context API.
/// No additional NuGet dependencies - uses HttpClient directly.
/// </summary>
public sealed class BraveSearchProvider : IWebSearchProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private const string BaseUrl = "https://api.search.brave.com/res/v1/web/search";

    public BraveSearchProvider(HttpClient httpClient, string apiKey)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = !string.IsNullOrWhiteSpace(apiKey)
            ? apiKey
            : throw new ArgumentException("API key cannot be null or empty.", nameof(apiKey));
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var requestUrl = $"{BaseUrl}?q={Uri.EscapeDataString(query)}&count=10&offset=0";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.Add("X-Subscription-Token", _apiKey);
        request.Headers.Add("Accept", "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<BraveSearchResponse>(json);

        if (result?.Web?.Results == null)
        {
            return Array.Empty<SearchResult>();
        }

        return result.Web.Results
            .Select(r => new SearchResult(
                r.Title ?? "",
                r.Url ?? "",
                r.Description ?? ""))
            .ToList();
    }

    // Brave Search API response models
    private sealed class BraveSearchResponse
    {
        [JsonPropertyName("web")]
        public WebResults? Web { get; set; }
    }

    private sealed class WebResults
    {
        [JsonPropertyName("results")]
        public List<WebResult>? Results { get; set; }
    }

    private sealed class WebResult
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }
}
