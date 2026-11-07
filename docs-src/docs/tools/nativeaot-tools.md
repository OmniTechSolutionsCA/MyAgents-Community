# NativeAOT Compile-Time Tools

The `MyAgents.Generators` package provides a Roslyn incremental source generator that translates C# methods marked with `[AgentTool]` into zero-reflection, compile-time tool bindings.

## Why Compile-Time Tools?

Traditional agent frameworks rely on runtime reflection and `System.Text.Json` runtime schema generation to discover method signatures, serialize parameters, and invoke functions.

In modern cloud, serverless, and edge environments, runtime reflection introduces:
1. **Trimming and NativeAOT Incompatibility**: Reflection-based serializers risk trimming warnings or runtime crashes when compiled with `PublishAot=true`.
2. **Cold Start Latency**: Reflection and JIT overhead add latency to the first tool execution.
3. **Allocation Overhead**: Dynamic method invocation allocates on every call.

`MyAgents.Generators` solves this by emitting:
- Fully static `ITool` classes.
- Compile-time constant strings for `ParametersSchemaJson`.
- Strongly-typed JSON context bindings.

---

## Authoring an [AgentTool]

### 1. Define Request and Response Models
Ensure types are serializable via `System.Text.Json`:

```csharp
using System.ComponentModel;
using System.Text.Json.Serialization;
using MyAgents.Core.Tools;

public sealed record CustomerInfoRequest(
    [Description("The unique customer ID")] string CustomerId);

public sealed record CustomerInfoResponse(
    string Name,
    string Tier,
    int OpenTickets);

[JsonSerializable(typeof(CustomerInfoRequest))]
[JsonSerializable(typeof(CustomerInfoResponse))]
internal sealed partial class CustomerJsonContext : JsonSerializerContext;
```

### 2. Implement the Tool Method

Annotate a static or instance method with `[AgentTool]`:

```csharp
public static class CustomerServiceTools
{
    [AgentTool(
        Description = "Retrieves account tier and open ticket counts for a customer.",
        JsonContext = typeof(CustomerJsonContext))]
    public static CustomerInfoResponse GetCustomerInfo(CustomerInfoRequest request)
    {
        return new CustomerInfoResponse(
            Name: "Acme Corp",
            Tier: "Enterprise",
            OpenTickets: 2);
    }
}
```

### 3. Register the Generated Tool

The source generator automatically creates an extension method `AsGetCustomerInfoTool()`:

```csharp
var definition = new AgentDefinition("support-agent", [
    AgentFeature.Create(assembly =>
    {
        // Add the generated compile-time tool
        assembly.Tools.Add(CustomerServiceToolsAgentToolsExtensions.AsGetCustomerInfoTool());
    })
]);
```

---

## Performance & Trimming Verification

Because the generated code references `CustomerJsonContext` directly, the .NET trimming analyzer detects that all types are statically known:

```xml
<PropertyGroup>
    <PublishAot>true</PublishAot>
    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
</PropertyGroup>
```

When building with `dotnet publish -c Release`, the compilation completes with **0 trim warnings and 0 AOT warnings**.
