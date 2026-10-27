using Microsoft.Extensions.AI;

namespace MyAgents.Agents;

/// <summary>
/// A registered model candidate available for adaptive multi-model routing.
/// </summary>
public sealed record ModelCandidate(
    string ModelId,
    IChatClient Client,
    ModelTier Tier = ModelTier.Standard,
    ModelPricing? Pricing = null,
    int? MaxInputTokens = null,
    bool IsFallback = false);