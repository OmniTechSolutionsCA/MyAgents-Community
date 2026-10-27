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
    /// <param name="tool">The tool to add.</param>
    /// <returns>The builder for fluent chaining.</returns>
    public ToolCatalogBuilder Add(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _tools.Add(tool);
        return this;
    }

    /// <summary>
    /// Builds the immutable tool catalog.
    /// </summary>
    /// <returns>A read-only list of tools.</returns>
    public IReadOnlyList<ITool> Build() => _tools.AsReadOnly();
}
