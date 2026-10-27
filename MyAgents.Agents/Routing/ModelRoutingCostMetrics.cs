namespace MyAgents.Agents;

/// <summary>
/// Structured telemetry recording for model selection, token consumption, and estimated cost.
/// </summary>
public sealed record ModelRoutingCostMetrics(
    string OperationId,
    string? SessionId,
    string SelectedModelId,
    ModelTier SelectedTier,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    decimal EstimatedCostUsd,
    TimeSpan Duration,
    bool FallbackTriggered,
    string? FallbackReason = null,
    DateTimeOffset Timestamp = default);