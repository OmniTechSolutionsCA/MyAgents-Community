namespace MyAgents.Core;

/// <summary>
/// Terminates the loop after a fixed number of turns.
/// </summary>
/// <remarks>
/// The check is inclusive: if <paramref name="maxTurns"/> is 3, the loop stops
/// after exactly 3 completed turns.
///
/// <code>
/// var condition = new MaxTurnsTermination(maxTurns: 5);
/// </code>
/// </remarks>
public sealed class MaxTurnsTermination : ITerminationCondition
{
    private readonly int _maxTurns;

    /// <param name="maxTurns">Maximum number of completed turns before termination.</param>
    public MaxTurnsTermination(int maxTurns)
    {
        if (maxTurns < 1)
            throw new ArgumentOutOfRangeException(nameof(maxTurns), "Must be at least 1.");
        _maxTurns = maxTurns;
    }

    /// <inheritdoc />
    public bool ShouldTerminate(AgentTurnResult result, int turnsCompleted)
        => turnsCompleted >= _maxTurns;
}

/// <summary>
/// Terminates the loop when any agent message contains a specific phrase.
/// </summary>
/// <remarks>
/// Follows the Semantic Kernel <c>TextMentionTerminationCondition</c> pattern.
/// The comparison is case-insensitive by default.
///
/// <code>
/// var condition = new TextMentionTermination("TERMINATE");
/// // Stops as soon as any output message contains the word "TERMINATE"
/// </code>
/// </remarks>
public sealed class TextMentionTermination : ITerminationCondition
{
    private readonly string _phrase;
    private readonly StringComparison _comparison;

    /// <param name="phrase">The text to look for in any output message.</param>
    /// <param name="comparison">String comparison to use (default: OrdinalIgnoreCase).</param>
    public TextMentionTermination(
        string phrase,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phrase);
        _phrase = phrase;
        _comparison = comparison;
    }

    /// <inheritdoc />
    public bool ShouldTerminate(AgentTurnResult result, int turnsCompleted)
    {
        foreach (var message in result.Output)
        {
            if (message.Content.Contains(_phrase, _comparison))
                return true;
        }
        return false;
    }
}

/// <summary>
/// Terminates the loop when the cumulative token usage exceeds a budget.
/// </summary>
/// <remarks>
/// Token counts are sourced from <see cref="AgentTurnTelemetry.TotalTokens"/> (PromptTokens + CompletionTokens).
/// Turns with no token data are skipped (not counted).
///
/// <code>
/// var condition = new TokenBudgetTermination(maxTokens: 8000);
/// </code>
/// </remarks>
public sealed class TokenBudgetTermination : ITerminationCondition
{
    private readonly int _maxTokens;
    private int _consumedTokens;

    /// <param name="maxTokens">Maximum cumulative tokens before termination.</param>
    public TokenBudgetTermination(int maxTokens)
    {
        if (maxTokens < 1)
            throw new ArgumentOutOfRangeException(nameof(maxTokens), "Must be at least 1.");
        _maxTokens = maxTokens;
    }

    /// <inheritdoc />
    public bool ShouldTerminate(AgentTurnResult result, int turnsCompleted)
    {
        if (result.Telemetry.TotalTokens.HasValue)
            _consumedTokens += result.Telemetry.TotalTokens.Value;

        return _consumedTokens >= _maxTokens;
    }

    /// <summary>
    /// Resets the cumulative token counter.
    /// Called automatically by <see cref="AgentLoop"/> before each run so this instance
    /// can be safely reused. Call manually if you share the instance outside of AgentLoop.
    /// </summary>
    public void Reset() => _consumedTokens = 0;
}
