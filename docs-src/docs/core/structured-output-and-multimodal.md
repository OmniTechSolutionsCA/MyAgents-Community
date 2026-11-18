# Structured Outputs & Multi-Modal Inputs

MyAgents Community supports schema-constrained JSON structured outputs with automatic self-correction retries, as well as multi-modal turns combining text, images, audio, video, and binary documents.

## 1. Strongly-Typed Structured Outputs

### Using `session.InvokeAsync<T>`

The `AgentSessionStructuredExtensions.InvokeAsync<T>` extension method configures the turn's `AgentResponseFormat` for type `T`, invokes the session, and deserializes the assistant's response into `AgentStructuredTurnResult<T>`:

```csharp
using MyAgents.Core;

public sealed record CodeReviewReport(
    string Summary,
    int SeverityScore,
    IReadOnlyList<string> Findings,
    bool ApprovedForMerge);

AgentStructuredTurnResult<CodeReviewReport> review = await session.InvokeAsync<CodeReviewReport>(
    messages: [AgentMessage.FromText("user", "Review the diff in PR #128.")],
    strict: true,
    cancellationToken: cancellationToken);

Console.WriteLine($"Approved: {review.Value.ApprovedForMerge} (Score: {review.Value.SeverityScore})");
foreach (var finding in review.Value.Findings)
{
    Console.WriteLine($"- {finding}");
}
```

### Using `StructuredOutputFeature<T>` with Self-Correction Retries

When an agent is dedicated to producing a specific schema—or when working with smaller models that occasionally emit markdown code fences (` ```json `) or invalid JSON—register `StructuredOutputFeature<T>` (from `MyAgents.Agents`).

`StructuredOutputMiddleware<T>` automatically:
1. Attaches `AgentResponseFormat.ForType<T>` to every turn's `TurnCapabilities`.
2. Strips surrounding markdown code fences if present.
3. Validates that the output deserializes cleanly into `T`.
4. If deserialization fails, appends a correction prompt containing the `JsonException` details and retries up to `maxValidationRetries` times.

```csharp
using MyAgents.Agents;
using MyAgents.Core;

var definition = new AgentDefinition("invoice-extractor", [
    new StructuredOutputFeature<InvoiceSummary>(
        maxValidationRetries: 2,
        schemaName: "invoice_summary",
        schemaDescription: "Extracted line items and totals from an invoice.",
        strict: true)
]);
```

### Custom JSON Schemas (`AgentResponseFormat`)

You can also specify raw JSON schemas or unconstrained JSON object mode on any turn:

```csharp
// Unconstrained JSON object mode
var jsonObjectFormat = AgentResponseFormat.JsonObject;

// Explicit JSON schema string
var customSchemaFormat = AgentResponseFormat.ForJsonSchema(
    schemaName: "ticket_classification",
    schemaJson: """
    {
      "type": "object",
      "properties": {
        "category": { "type": "string", "enum": ["billing", "technical", "sales"] },
        "urgency": { "type": "integer" }
      },
      "required": ["category", "urgency"],
      "additionalProperties": false
    }
    """,
    strict: true);

var result = await session.InvokeAsync(
    [AgentMessage.FromText("user", "Classify: My server is down!")],
    configureContext: ctx => ctx.Capabilities.Set(customSchemaFormat),
    cancellationToken: cancellationToken);
```

---

## 2. Multi-Modal Messages (`AgentContent`)

`AgentMessage.FromParts` lets you combine multiple `AgentContent` items in a single turn. `ChatClientRuntime` maps these directly to `Microsoft.Extensions.AI` content types (`TextContent`, `DataContent`, `UriContent`):

| Content Type | Purpose | Key Properties |
| :--- | :--- | :--- |
| **`TextAgentContent`** | Natural language text segment | `Text` |
| **`ImageAgentContent`** | Inline binary image bytes | `Data`, `MediaType` (e.g. `"image/png"`) |
| **`UriImageAgentContent`** | Remote or local URI image reference | `Uri`, `MediaType` |
| **`AudioAgentContent`** | Audio clip bytes | `Data`, `MediaType`, `Duration`, `Language` |
| **`VideoAgentContent`** | Video clip bytes | `Data`, `MediaType`, `Duration`, `Width`, `Height` |
| **`DocumentAgentContent`** | Named document attachment (PDF, CSV, etc.) | `Data`, `MediaType`, `FileName` |
| **`DataAgentContent`** | Arbitrary binary payload | `Data`, `MediaType` |

### Example: Sending Text + Image + PDF Document

```csharp
using MyAgents.Core;

byte[] screenshotBytes = await File.ReadAllBytesAsync("error-dialog.png", cancellationToken);
byte[] logFileBytes = await File.ReadAllBytesAsync("crash-report.pdf", cancellationToken);

var multiModalMessage = AgentMessage.FromParts(
    role: "user",
    parts:
    [
        new TextAgentContent("Diagnose the error shown in this screenshot and crash report."),
        new ImageAgentContent(screenshotBytes, "image/png"),
        new DocumentAgentContent(logFileBytes, "application/pdf", "crash-report.pdf")
    ],
    textSummary: "Diagnose the error shown in this screenshot and crash report.");

var result = await session.InvokeAsync([multiModalMessage], cancellationToken);
```
