using Microsoft.Extensions.AI;
using MyAgents.Core;
using MyAgents.Core.Memory;

namespace MyAgents.Memory;

/// <summary>
/// Agent feature that contributes a <see cref="VectorMemoryStore"/> as the
/// agent's <see cref="IMemoryStore"/> (exposed via <see cref="AgentAssembly.Memory"/>).
/// </summary>
/// <remarks>
/// Register at composition root:
/// <code>
/// var agent = new AgentDefinition(
///     Name: "ResearchAgent",
///     Features: new IAgentFeature[]
///     {
///         new VectorMemoryFeature(embeddingGenerator),
///         // ...
///     });
/// </code>
/// Access during a turn via <c>context.Services.Memory</c>.
/// </remarks>
public sealed class VectorMemoryFeature : IAgentFeature
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;

    /// <summary>
    /// Creates a <see cref="VectorMemoryFeature"/> that uses the given embedding
    /// generator to power semantic search.
    /// </summary>
    public VectorMemoryFeature(IEmbeddingGenerator<string, Embedding<float>> embedder)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
    }

    /// <inheritdoc/>
    public void Contribute(AgentAssembly assembly)
    {
        assembly.Memory = new VectorMemoryStore(_embedder);
    }
}
