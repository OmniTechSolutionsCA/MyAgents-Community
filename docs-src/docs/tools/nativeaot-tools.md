# NativeAOT Compile-Time Tools

The `MyAgents.Generators` package provides a Roslyn incremental source generator that translates C# methods marked with `[AgentTool]` into zero-reflection, compile-time `ITool` bindings.

## Why Compile-Time Tools?

Traditional agent frameworks rely on runtime reflection and `System.Text.Json` runtime schema reflection to discover method signatures, serialize parameters, and invoke functions.

In cloud, serverless, CLI, and edge environments, runtime reflection introduces:
1. **Trimming and NativeAOT Incompatibility**: Reflection-based serializers trigger `IL2026` / `IL3050` warnings or fail at runtime when compiled with `<PublishAot>true</PublishAot>`.
2. **Cold Start Latency**: Dynamic reflection and expression tree compilation add startup overhead.
3. **Allocation Overhead**: Boxing and `MethodInfo.Invoke` allocate on every tool call.

`MyAgents.Generators` solves this at compile time by emitting:
- Strongly-typed `ITool` wrapper classes (`<ClassName>_<MethodName>_AgentTool`).
- Extension methods (`As<MethodName>Tool()`) for one-line registration.
- Compile-time JSON schemas (`ParametersSchemaJson`) and NativeAOT-safe serialization via your `JsonSerializerContext`.

---

## Authoring an `[AgentTool]`

### 1. Define Request and Response Models

For complex parameter and return types, declare a `JsonSerializerContext` so `System.Text.Json` generates source-generated metadata at compile time:

```csharp
using System.ComponentModel;
using System.Text.Json.Serialization;
using MyAgents.Core.Tools;

public sealed record TicketRequest(string Subject, int Priority = 3);

public sealed record TicketReport(int Id, string Subject, int Priority, string Status);

[JsonSerializable(typeof(TicketRequest))]
[JsonSerializable(typeof(TicketReport))]
[JsonSerializable(typeof(TicketReport[]))]
internal sealed partial class TicketJsonContext : JsonSerializerContext;
```

### 2. Annotate Tool Methods with `[AgentTool]`

Annotate static or instance methods with `[AgentTool]` and point `JsonContext` to your `JsonSerializerContext`:

```csharp
public static class SupportTools
{
    [AgentTool(Description = "Creates a support ticket", JsonContext = typeof(TicketJsonContext))]
    public static TicketReport CreateTicket(
        [Description("The ticket request")] TicketRequest request)
        => new(Id: 42, request.Subject, request.Priority, "open");

    [AgentTool(Description = "Lists open support tickets", JsonContext = typeof(TicketJsonContext))]
    public static async Task<TicketReport[]> ListOpenTicketsAsync(
        CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return [new(42, "Printer on fire", 1, "open")];
    }
}
```

> [!TIP]
> Primitive parameters (`string`, `int`, `long`, `double`, `bool`, enums, etc.) are bound directly from `JsonDocument` with zero reflection even without a `JsonContext`. When using complex record or class types, `MyAgents.Generators` emits analyzer diagnostics (`MYAG001` / `MYAG002`) if a `JsonSerializerContext` registration is missing.

### 3. Register the Generated Tools

The source generator emits an extension class `<ContainingClass>AgentToolsExtensions` with `As<MethodName>Tool()` helper methods:

```csharp
using MyAgents.Agents;
using MyAgents.Core;

var definition = new AgentDefinition("support-agent", [
    new ToolsFeature(
        SupportToolsAgentToolsExtensions.AsCreateTicketTool(),
        SupportToolsAgentToolsExtensions.AsListOpenTicketsAsyncTool())
]);
```

If your tool methods are instance methods on a service class (for example `CustomerService`), pass the instance to the generated extension method:

```csharp
var customerService = serviceProvider.GetRequiredService<CustomerService>();
var tool = customerService.AsGetCustomerInfoTool();
```

---

## Publishing as a NativeAOT Executable

Enable NativeAOT in your `.csproj`:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net10.0</TargetFramework>
  <PublishAot>true</PublishAot>
  <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
</PropertyGroup>
```

Publish and run the native binary (see [`community/samples/NativeAotTools`](https://github.com/OmniTechSolutionsCA/MyAgents-Community/tree/main/community/samples/NativeAotTools)):

```bash
dotnet publish community/samples/NativeAotTools -c Release
```

The compilation finishes with **0 trim warnings and 0 AOT warnings**.
