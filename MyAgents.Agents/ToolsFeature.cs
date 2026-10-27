using System.Text.Json;
using MyAgents.Core;
using MyAgents.Core.Tools;

namespace MyAgents.Agents;

/// <summary>
/// Feature that registers one or more <see cref="ITool"/> instances or delegates on an agent definition.
/// </summary>
public sealed class ToolsFeature : IAgentFeature
{
    private readonly List<ITool> _tools = new();

    /// <summary>
    /// Initializes an empty <see cref="ToolsFeature"/>.
    /// </summary>
    public ToolsFeature()
    {
    }

    /// <summary>
    /// Initializes a <see cref="ToolsFeature"/> with the given tools.
    /// </summary>
    /// <param name="tools">The tools to register.</param>
    public ToolsFeature(params ITool[] tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools.AddRange(tools);
    }

    /// <summary>
    /// Registers a tool instance.
    /// </summary>
    /// <param name="tool">The tool to register.</param>
    /// <returns>This feature for fluent chaining.</returns>
    public ToolsFeature Add(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _tools.Add(tool);
        return this;
    }

    /// <summary>
    /// Registers a delegate function as an agent tool with automatic schema generation and argument binding.
    /// </summary>
    /// <param name="method">The delegate to execute.</param>
    /// <param name="name">Optional tool name. Defaults to method name.</param>
    /// <param name="description">Optional tool description.</param>
    /// <param name="jsonSerializerOptions">Optional JSON serializer options.</param>
    /// <returns>This feature for fluent chaining.</returns>
    public ToolsFeature AddFunction(
        Delegate method,
        string? name = null,
        string? description = null,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        _tools.Add(DelegateTool.Create(method, name, description, jsonSerializerOptions));
        return this;
    }

    /// <inheritdoc />
    public void Contribute(AgentAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        foreach (var tool in _tools)
        {
            assembly.Tools.Add(tool);
        }

        if (_tools.Count > 0)
        {
            assembly.Capabilities.Add(AgentCapabilities.Tools);
        }
    }
}
