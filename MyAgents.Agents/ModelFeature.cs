using MyAgents.Core;

namespace MyAgents.Agents;

/// <summary>
/// Declares a preferred model for the current agent.
/// </summary>
/// <remarks>
/// The runtime/provider decides how this model ID is resolved. For example,
/// <c>MafRuntime</c> can be configured with a model resolver function that maps
/// this value to a specific <see cref="Microsoft.Extensions.AI.IChatClient"/>.
/// </remarks>
public sealed class ModelFeature : IAgentFeature
{
    private readonly string _modelId;

    /// <param name="modelId">Model identifier (for example <c>gpt-4o</c>).</param>
    public ModelFeature(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        _modelId = modelId;
    }

    /// <inheritdoc />
    public void Contribute(AgentAssembly assembly)
        => assembly.Capabilities.Add(new AgentModelCapability(_modelId));
}
