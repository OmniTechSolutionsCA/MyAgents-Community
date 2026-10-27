using Microsoft.Extensions.AI;

namespace MyAgents.Agents;

/// <summary>
/// Configuration options for multi-model routing and token/cost optimization.
/// </summary>
public sealed class MultiModelRoutingOptions
{
    /// <summary>
    /// Maximum allowable total tokens for a single turn (null for unlimited).
    /// </summary>
    public long? MaxTokensPerTurn { get; set; }

    /// <summary>
    /// Maximum allowable cost in USD for a single turn (null for unlimited).
    /// </summary>
    public decimal? MaxCostPerTurnUsd { get; set; }

    /// <summary>
    /// Maximum allowable cumulative tokens across an entire session (null for unlimited).
    /// </summary>
    public long? MaxTokensPerSession { get; set; }

    /// <summary>
    /// Maximum allowable cumulative cost in USD across an entire session (null for unlimited).
    /// </summary>
    public decimal? MaxCostPerSessionUsd { get; set; }

    /// <summary>
    /// Whether automatic fallback cascading to higher or designated fallback models is enabled on execution failure.
    /// Default is true.
    /// </summary>
    public bool EnableFallbackCascade { get; set; } = true;

    /// <summary>
    /// Optional custom selector function to override default heuristic-based routing.
    /// Returns the chosen <see cref="ModelCandidate"/>, or null to fall back to built-in heuristic routing.
    /// </summary>
    public Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyList<ModelCandidate>, ModelCandidate?>? CustomSelector { get; set; }
}