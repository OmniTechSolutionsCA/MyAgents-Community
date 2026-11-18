# Delegate Tools, Custom Tools & Built-in Capabilities

In addition to compile-time `[AgentTool]` source generation, MyAgents Community lets you register in-process C# delegates via `ToolsFeature` and `DelegateTool`, implement `ITool` directly, or attach built-in web search and process-execution tools.

## 1. Using `ToolsFeature` and `DelegateTool`

`ToolsFeature` (in `MyAgents.Agents`) and `DelegateTool.Create` (in `MyAgents.Core.Tools`) wrap any synchronous or asynchronous C# `Delegate`:

```csharp
using System.ComponentModel;
using MyAgents.Agents;
using MyAgents.Core;
using MyAgents.Core.Tools;

// 1. Create a DelegateTool directly
var timeTool = DelegateTool.Create(
    method: () => DateTimeOffset.UtcNow.ToString("O"),
    name: "get_current_utc_time",
    description: "Returns the current UTC date and time in ISO 8601 format.");

// 2. Or register functions fluently on ToolsFeature
var toolsFeature = new ToolsFeature(timeTool)
    .AddFunction(
        ([Description("Subtotal in dollars")] decimal subtotal,
         [Description("Tax rate (e.g. 0.05 for 5%)")] decimal taxRate) =>
        {
            var tax = subtotal * taxRate;
            return new { Subtotal = subtotal, Tax = tax, Total = subtotal + tax };
        },
        name: "calculate_tax",
        description: "Calculates total price after tax.");

var definition = new AgentDefinition("billing-assistant", [toolsFeature]);
```

### Async Handlers & `CancellationToken` Injection

`DelegateTool` automatically binds `CancellationToken` parameters from the turn's cancellation token and awaits `Task<T>` or `ValueTask<T>` return values:

```csharp
var queryTool = DelegateTool.Create(
    method: async ([Description("Search keyword")] string query, CancellationToken ct) =>
    {
        var rows = await myRepository.SearchAsync(query, ct);
        return rows;
    },
    name: "query_customers",
    description: "Searches customer records asynchronously.");
```

---

## 2. Implementing `ITool` Directly

For stateful tools, custom JSON parsing, or zero-reflection libraries without source generators, implement `MyAgents.Core.Tools.ITool`:

```csharp
using System.Text.Json;
using MyAgents.Core.Tools;

public sealed class SqlQueryTool : ITool
{
    public string Name => "execute_readonly_sql";

    public string? Description => "Executes a read-only SELECT query against the reporting database.";

    public string? ParametersSchemaJson => """
    {
      "type": "object",
      "properties": {
        "sql": {
          "type": "string",
          "description": "The read-only SELECT query"
        }
      },
      "required": ["sql"],
      "additionalProperties": false
    }
    """;

    public async ValueTask<string> InvokeAsync(
        string jsonArguments,
        CancellationToken cancellationToken = default)
    {
        using var doc = JsonDocument.Parse(jsonArguments);
        var sql = doc.RootElement.GetProperty("sql").GetString()
            ?? throw new ArgumentException("Missing required 'sql' property.");

        // Execute query and return serialized JSON string
        var rows = await ExecuteQueryAsync(sql, cancellationToken);
        return JsonSerializer.Serialize(rows);
    }

    private static Task<object[]> ExecuteQueryAsync(string sql, CancellationToken ct)
        => Task.FromResult<object[]>([new { RowCount = 1, Query = sql }]);
}
```

---

## 3. Built-in Web Search (`WebSearchFeature`)

`MyAgents.Agents.WebSearch` provides `IWebSearchProvider`, `BraveSearchProvider`, and `WebSearchFeature` to equip an agent with a `web_search` tool:

```csharp
using MyAgents.Agents.WebSearch;
using MyAgents.Core;

using var httpClient = new HttpClient();
var searchProvider = new BraveSearchProvider(
    httpClient,
    apiKey: Environment.GetEnvironmentVariable("BRAVE_API_KEY")!);

var definition = new AgentDefinition("research-agent", [
    new WebSearchFeature(searchProvider)
]);
```

You can also implement `IWebSearchProvider` to plug in any custom search engine or internal enterprise search API:

```csharp
public sealed class InternalWikiSearchProvider : IWebSearchProvider
{
    public Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SearchResult> results =
        [
            new SearchResult(
                Title: "Runbook: Database Failover",
                Url: "https://wiki.internal/db-failover",
                Snippet: "Step-by-step instructions for regional failover.")
        ];
        return Task.FromResult(results);
    }
}
```

---

## 4. Subprocess & Crawler Tools (`IProcessRunner` & `ScrapaiFeature`)

`MyAgents.Core.Tools` includes `IProcessRunner` and `DefaultProcessRunner` for safe, testable CLI subprocess execution, which powers `ScrapaiFeature` (`scrapai_crawl`, `scrapai_list_spiders`, `scrapai_run`):

```csharp
using MyAgents.Agents.Scrapai;
using MyAgents.Core;
using MyAgents.Core.Tools;

var runner = new DefaultProcessRunner();
var definition = new AgentDefinition("crawler-agent", [
    new ScrapaiFeature(runner, executablePath: "scrapai")
]);
```
