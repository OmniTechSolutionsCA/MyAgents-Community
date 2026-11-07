# Delegate Tools & Custom Tools

In addition to compile-time source generated tools, MyAgents Community allows you to define dynamic in-process tools using `DelegateTool` or implement the `ITool` interface directly.

## Using `DelegateTool`

`DelegateTool` is the fastest way to expose existing C# lambdas or methods as tools:

```csharp
using System.ComponentModel;
using MyAgents.Core.Tools;

// Parameterless tool
var timeTool = DelegateTool.Create(
    name: "get_current_utc_time",
    description: "Returns the current UTC date and time in ISO 8601 format.",
    handler: () => DateTimeOffset.UtcNow.ToString("O")
);

// Tool with parameters and descriptions
var calculatorTool = DelegateTool.Create(
    name: "calculate_tax",
    description: "Calculates total price after tax.",
    handler: (
        [Description("Subtotal in dollars")] decimal subtotal,
        [Description("Tax rate percentage (e.g. 0.05 for 5%)")] decimal taxRate) =>
    {
        var tax = subtotal * taxRate;
        return new { Subtotal = subtotal, Tax = tax, Total = subtotal + tax };
    }
);
```

### Async Handlers and Cancellation Tokens

Delegate tools support asynchronous methods and automatically receive a `CancellationToken`:

```csharp
var searchTool = DelegateTool.Create(
    name: "query_database",
    description: "Queries the customer database asynchronously.",
    handler: async (string query, CancellationToken ct) =>
    {
        return await myDbService.ExecuteQueryAsync(query, ct);
    }
);
```

---

## Implementing `ITool` Directly

For complex stateful tools or reusable tool libraries, implement `ITool`:

```csharp
using System.Text.Json;
using MyAgents.Core.Tools;

public sealed class DatabaseQueryTool : ITool
{
    public string Name => "execute_sql";
    public string Description => "Executes a read-only SQL query against the analytics database.";
    
    public string ParametersSchemaJson => """
    {
      "type": "object",
      "properties": {
        "sql": {
          "type": "string",
          "description": "The read-only SELECT query"
        }
      },
      "required": ["sql"]
    }
    """;

    private readonly DbConnection _connection;

    public DatabaseQueryTool(DbConnection connection)
    {
        _connection = connection;
    }

    public async ValueTask<ToolExecutionResult> InvokeAsync(
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        var sql = doc.RootElement.GetProperty("sql").GetString();

        var result = await _connection.QueryAsync(sql, cancellationToken);
        return ToolExecutionResult.Success(JsonSerializer.Serialize(result));
    }
}
```

---

## Tool Categories and Tags

You can tag tools to group them for permission policies or telemetry:

```csharp
assembly.Tools.Add(timeTool);
assembly.Tools.Add(calculatorTool);
```
