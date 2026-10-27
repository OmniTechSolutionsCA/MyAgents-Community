namespace MyAgents.Agents;

/// <summary>
/// Pricing specifications for a model candidate (USD per 1,000,000 tokens).
/// </summary>
public sealed record ModelPricing(
    string ModelId,
    decimal InputCostPerMillionTokens,
    decimal OutputCostPerMillionTokens)
{
    /// <summary>
    /// Calculates the estimated cost in USD for a given token volume.
    /// </summary>
    public decimal CalculateCost(long inputTokens, long outputTokens)
    {
        var inputCost = (inputTokens / 1_000_000m) * InputCostPerMillionTokens;
        var outputCost = (outputTokens / 1_000_000m) * OutputCostPerMillionTokens;
        return decimal.Round(inputCost + outputCost, 6, MidpointRounding.AwayFromZero);
    }
}