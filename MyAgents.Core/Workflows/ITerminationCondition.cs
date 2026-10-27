namespace MyAgents.Core;

/// <summary>
/// Determines whether an agent loop or workflow handoff chain should terminate.
/// </summary>
/// <remarks>
/// Inspired by Semantic Kernel's <c>TerminationCondition</c> and AutoGen's
/// <c>TerminationCondition</c> protocol. Implementations are composable:
/// use <see cref="Any"/> or <see cref="All"/> to combine conditions.
///
/// <code>
/// // Stop after 5 turns OR if the assistant says "DONE"
/// var condition = ITerminationCondition.Any(
///     new MaxTurnsTermination(maxTurns: 5),
///     new TextMentionTermination("DONE"));
/// </code>
/// </remarks>
public interface ITerminationCondition
{
    /// <summary>
    /// Evaluates whether the loop should terminate given the latest result
    /// and the number of turns completed so far (0-based on the first call).
    /// </summary>
    /// <param name="result">The result just produced by the agent.</param>
    /// <param name="turnsCompleted">Number of successful turns completed, including the current one.</param>
    /// <returns><c>true</c> if the loop should stop; <c>false</c> to continue.</returns>
    bool ShouldTerminate(AgentTurnResult result, int turnsCompleted);

    /// <summary>
    /// Resets any internal state accumulated across turns (e.g. cumulative token counts).
    /// Called automatically by <see cref="AgentLoop"/> before each run so that the same
    /// condition instance can be safely reused across multiple loop invocations.
    /// The default implementation is a no-op.
    /// </summary>
    void Reset() { }

    /// <summary>
    /// Returns a condition that terminates when <b>any</b> of the given conditions triggers.
    /// </summary>
    public static ITerminationCondition Any(params ITerminationCondition[] conditions)
        => new AnyTermination(conditions);

    /// <summary>
    /// Returns a condition that terminates only when <b>all</b> of the given conditions trigger.
    /// </summary>
    public static ITerminationCondition All(params ITerminationCondition[] conditions)
        => new AllTermination(conditions);

    // ── Composite implementations ─────────────────────────────────────────────

    private sealed class AnyTermination(IReadOnlyList<ITerminationCondition> conditions)
        : ITerminationCondition
    {
        public bool ShouldTerminate(AgentTurnResult result, int turnsCompleted)
        {
            foreach (var c in conditions)
                if (c.ShouldTerminate(result, turnsCompleted)) return true;
            return false;
        }

        public void Reset()
        {
            foreach (var c in conditions) c.Reset();
        }
    }

    private sealed class AllTermination(IReadOnlyList<ITerminationCondition> conditions)
        : ITerminationCondition
    {
        public bool ShouldTerminate(AgentTurnResult result, int turnsCompleted)
        {
            foreach (var c in conditions)
                if (!c.ShouldTerminate(result, turnsCompleted)) return false;
            return true;
        }

        public void Reset()
        {
            foreach (var c in conditions) c.Reset();
        }
    }
}
