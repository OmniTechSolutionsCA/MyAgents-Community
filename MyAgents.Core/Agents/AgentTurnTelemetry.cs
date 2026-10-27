namespace MyAgents.Core;

/// <summary>
/// Telemetry data captured during an agent turn.
/// </summary>
/// <param name="TurnDuration">Duration of the turn execution.</param>
/// <param name="ToolCalls">Number of tool invocations during the turn.</param>
/// <param name="PromptTokens">
/// Number of tokens in the prompt (input) sent to the model, if available.
/// </param>
/// <param name="CompletionTokens">
/// Number of tokens in the completion (output) returned by the model, if available.
/// </param>
/// <param name="ModelId">The LLM model used for this turn.</param>
public sealed record AgentTurnTelemetry(
    TimeSpan TurnDuration,
    int ToolCalls = 0,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    string? ModelId = null)
{
    /// <summary>
    /// Total token usage (prompt + completion). <see langword="null"/> when neither
    /// <see cref="PromptTokens"/> nor <see cref="CompletionTokens"/> is available.
    /// </summary>
    public int? TotalTokens =>
        PromptTokens.HasValue || CompletionTokens.HasValue
            ? (PromptTokens ?? 0) + (CompletionTokens ?? 0)
            : null;
}
