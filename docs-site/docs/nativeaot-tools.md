# NativeAOT Tools

`MyAgents.Generators` provides a Roslyn incremental source generator that analyzes `[AgentTool]` attributes on static or instance methods.

## Why NativeAOT Tools?

In traditional frameworks, tool invocation relies heavily on runtime reflection and runtime JSON schema generation via `System.Text.Json` reflection. This causes:
1. Startup latency and JIT compilation overhead.
2. High memory allocations on every tool call.
3. Incompatibility with .NET NativeAOT compilation and trimming.

With `MyAgents.Generators`, all JSON schemas and argument bindings are evaluated at **compile time**.

## Authoring an [AgentTool]

To author a NativeAOT-safe tool:

1. Define your parameter and return records.
2. Create a `JsonSerializerContext` referencing the types.
3. Annotate the tool method with `[AgentTool]`:

```csharp
using System.ComponentModel;
using System.Text.Json.Serialization;
using MyAgents.Core.Tools;

public sealed record SupportTicket(string Subject, int Priority = 3);
public sealed record TicketResult(int Id, string Status);

[JsonSerializable(typeof(SupportTicket))]
[JsonSerializable(typeof(TicketResult))]
internal sealed partial class TicketJsonContext : JsonSerializerContext;

public static class SupportTools
{
    [AgentTool(
        Description = "Creates a new customer support ticket",
        JsonContext = typeof(TicketJsonContext))]
    public static TicketResult CreateTicket(
        [Description("The ticket request")] SupportTicket request)
    {
        return new TicketResult(Id: 42, Status: "open");
    }
}
```

## Generated Code

The source generator automatically generates:
- An `ITool` implementation (`CreateTicketTool`) wrapping the method.
- A compile-time constant string for `ParametersSchemaJson` containing the exact JSON schema of `SupportTicket`.
- An extension method `SupportToolsAgentToolsExtensions.AsCreateTicketTool()` for registration.

```csharp
// Register the compile-time tool directly
assembly.Tools.Add(SupportToolsAgentToolsExtensions.AsCreateTicketTool());
```
