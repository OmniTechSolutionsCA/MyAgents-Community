namespace MyAgents.Core;

/// <summary>
/// Declares the preferred model ID for an agent.
/// </summary>
/// <param name="ModelId">Model identifier understood by the runtime or provider.</param>
public sealed record AgentModelCapability(string ModelId) : IAgentCapability
{
    /// <inheritdoc/>
    public string Id => "agent.model";

    /// <inheritdoc/>
    public string Description => $"Preferred model: {ModelId}";
}
