using System.Text.Json;
using MyAgents.Core.Tools;

namespace MyAgents.Core;

/// <summary>
/// Builder for registering tools during agent assembly.
/// </summary>
public sealed class ToolCatalogBuilder
{
    private readonly List<ITool> _tools = new();

    /// <summary>
    /// Adds a tool to the catalog.
    /// </summary>
    /// <param name=\"tool\">The tool to add.</param>
    /// <returns>The builder for fluent chaining.</returns>
    public ToolCatalogBuilder Add(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _tools.Add(tool);
        return this;
    }

    /// <summary>
    /// Adds a delegate as a tool to the catalog with automatic schema generation and argument binding.
    /// </summary>
    /// <param name=\"method\">The delegate to execute.</param>
    /// <param name=\"name\">Optional tool name. Defaults to the method name.</param>
    /// <param name=\"description\">Optional tool description.</param>
    /// <param name=\"jsonSerializerOptions\">Optional JSON serializer options.</param>
    /// <returns>The builder for fluent chaining.</returns>
    public ToolCatalogBuilder AddFunction(
        Delegate method,
        string? name = null,
        string? description = null,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        _tools.Add(DelegateTool.Create(method, name, description, jsonSerializerOptions));
        return this;
    }

    /// <summary>
    /// Builds the immutable tool catalog.
    /// </summary>
    /// <returns>A read-only list of tools.</returns>
    public IReadOnlyList<ITool> Build() => _tools.AsReadOnly();
}
