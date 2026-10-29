# MyAgents.Generators

Roslyn incremental source generator for the MyAgents Community agent framework.

Generates compile-time `ITool` wrappers for methods annotated with `[AgentTool]`:
JSON schemas as constants, reflection-free primitive argument binding, and
NativeAOT-safe serialization of complex types via `JsonSerializerContext`.

## Usage

Reference this package alongside `MyAgents.Core`:

```xml
<PackageReference Include="MyAgents.Core" Version="0.2.0-*" />
<PackageReference Include="MyAgents.Generators" Version="0.2.0-*" />
```

Then annotate methods:

```csharp
using MyAgents.Core.Tools;

public static class WeatherTools
{
    [AgentTool(Description = "Gets current weather for a city")]
    public static string GetWeather(string city) => $"{city}: 22C";
}

// Register the generated tool
var tool = WeatherToolsAgentToolsExtensions.AsGetWeatherTool();
```

Complex parameter and result types should point at a `JsonSerializerContext`
for NativeAOT/trimming-safe binding and exporter-accurate schemas:

```csharp
[JsonSerializable(typeof(WeatherRequest))]
[JsonSerializable(typeof(WeatherReport))]
internal sealed partial class WeatherJsonContext : JsonSerializerContext;

[AgentTool(Description = "Gets weather", JsonContext = typeof(WeatherJsonContext))]
public static WeatherReport GetWeather(WeatherRequest request) => ...;
```

See the [repository](https://github.com/OmniTechSolutionsCA/MyAgents-Community) for
the full documentation, diagnostics reference (MYAG001/MYAG002), and samples.
