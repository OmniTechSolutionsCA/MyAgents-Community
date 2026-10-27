namespace MyAgents.Core;

/// <summary>
/// Definition of an agent, composed of features that contribute tools, middleware, and behavior.
/// </summary>
/// <param name="Name">The agent's name.</param>
/// <param name="Features">Features that contribute to this agent's behavior.</param>
/// <param name="Description">Optional description of what the agent does.</param>
/// <param name="Capabilities">
/// Optional list of capabilities this agent declares.
/// Orchestrators and routers can inspect these to make routing decisions
/// without having to know each feature type.
/// Use <see cref="AgentCapabilities"/> for well-known constant values.
/// </param>
public sealed record AgentDefinition(
    string Name,
    IReadOnlyList<IAgentFeature> Features,
    string? Description = null,
    IReadOnlyList<IAgentCapability>? Capabilities = null)
{
    /// <summary>
    /// Returns true if this agent declares the capability identified by <paramref name="capabilityId"/>.
    /// </summary>
    public bool HasCapability(string capabilityId)
        => Capabilities is { Count: > 0 }
           && Capabilities.Any(c => string.Equals(c.Id, capabilityId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns true if this agent declares the given well-known capability.
    /// </summary>
    public bool HasCapability(IAgentCapability capability)
        => HasCapability(capability.Id);
}
