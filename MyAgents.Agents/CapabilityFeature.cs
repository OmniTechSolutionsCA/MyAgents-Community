using MyAgents.Core;

namespace MyAgents.Agents;

/// <summary>
/// Feature that declares one or more <see cref="IAgentCapability"/> entries for an agent.
/// Capabilities are stored on <see cref="AgentAssembly.Capabilities"/> so that orchestrators,
/// routers, and tooling can discover what the agent supports at runtime without inspecting
/// the full feature list.
/// </summary>
/// <remarks>
/// Usage — explicit declaration:
/// <code>
/// var agent = new AgentDefinition(
///     Name: "VisionAgent",
///     Features: new IAgentFeature[]
///     {
///         new CapabilityFeature(AgentCapabilities.MultiModal, AgentCapabilities.Tools),
///         new WebSearchFeature(provider),
///     });
/// </code>
///
/// Query at runtime:
/// <code>
/// if (definition.HasCapability(AgentCapabilities.MultiModal))
///     Console.WriteLine("Agent supports images.");
/// </code>
/// </remarks>
public sealed class CapabilityFeature : IAgentFeature
{
    private readonly IReadOnlyList<IAgentCapability> _capabilities;

    /// <summary>
    /// Declares a fixed set of capabilities for the agent.
    /// </summary>
    public CapabilityFeature(params IAgentCapability[] capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        _capabilities = capabilities;
    }

    /// <inheritdoc />
    public void Contribute(AgentAssembly assembly)
    {
        foreach (var cap in _capabilities)
            assembly.Capabilities.Add(cap);
    }
}
