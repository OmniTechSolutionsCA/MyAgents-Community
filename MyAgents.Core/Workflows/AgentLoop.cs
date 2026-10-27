namespace MyAgents.Core.Workflows;

/// <summary>
/// A standalone ReAct-style agent loop that drives a single agent session until a
/// termination condition is met or <paramref name="maxTurns"/> is exhausted.
///
/// This is the building block for single-agent reasoning loops that don't require
/// the full <see cref="WorkflowHost"/> / <see cref="IWorkflow{TState}"/> machinery.
/// For multi-agent orchestration use <see cref="WorkflowHost"/> instead.
/// </summary>
/// <example>
/// <code>
/// var result = await AgentLoop.RunAsync(
///     session,
///     initialMessages: new[] { new AgentMessage("user", "Research AI agent frameworks.") },
///     termination: ITerminationCondition.Any(
///         new MaxTurnsTermination(maxTurns: 10),
///         new TextMentionTermination("DONE")),
///     maxTurns: 10);
///
/// Console.WriteLine(result.FinalResult.Output[0].Content);
/// </code>
/// </example>
public static class AgentLoop
{
    /// <summary>
    /// Runs the agent in a loop until the termination condition is met or <paramref name="maxTurns"/>
    /// is exceeded, whichever comes first.
    /// </summary>
    /// <param name="session">The agent session to invoke on each turn.</param>
    /// <param name="initialMessages">The messages to send on the first turn.</param>
    /// <param name="termination">
    /// Condition checked after each turn. When it returns <c>true</c> the loop stops.
    /// Use <see cref="ITerminationCondition.Any"/> / <see cref="ITerminationCondition.All"/>
    /// to combine conditions.
    /// </param>
    /// <param name="maxTurns">Hard upper bound on turns. Defaults to 20.</param>
    /// <param name="onTurnCompleted">
    /// Optional callback invoked after each turn, before termination is evaluated.
    /// Useful for logging or streaming partial results to the caller.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// An <see cref="AgentLoopResult"/> containing all turn results, the final result,
    /// the termination reason, and the number of turns completed.
    /// </returns>
    public static async ValueTask<AgentLoopResult> RunAsync(
        IAgentSession session,
        IReadOnlyList<AgentMessage> initialMessages,
        ITerminationCondition termination,
        int maxTurns = 20,
        Action<AgentTurnResult, int>? onTurnCompleted = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(initialMessages);
        ArgumentNullException.ThrowIfNull(termination);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTurns, 1);

        // Reset any stateful conditions (e.g. TokenBudgetTermination) so this call
        // is safe regardless of whether the condition was used in a previous run.
        termination.Reset();

        var allResults = new List<AgentTurnResult>();
        var currentMessages = initialMessages;
        AgentLoopTerminationReason reason = AgentLoopTerminationReason.MaxTurnsReached;

        for (int turn = 1; turn <= maxTurns; turn++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await session.InvokeAsync(currentMessages, cancellationToken)
                .ConfigureAwait(false);

            allResults.Add(result);
            onTurnCompleted?.Invoke(result, turn);

            if (termination.ShouldTerminate(result, turn))
            {
                reason = AgentLoopTerminationReason.ConditionMet;
                return new AgentLoopResult(allResults, result, reason, turn);
            }

            // Feed the agent's output back as the input for the next turn.
            // This enables tool-use / reflection cycles where the agent's own
            // output drives subsequent reasoning steps.
            currentMessages = result.Output;
        }

        return new AgentLoopResult(
            allResults,
            allResults[^1],
            reason,
            maxTurns);
    }

    // ── Typed-state overload ───────────────────────────────────────────────────

    /// <summary>
    /// Runs the agent in a loop with typed state that is updated by a reducer after
    /// every turn. Useful for ReAct-style loops where the agent accumulates structured
    /// results (e.g. a research summary, a tool output list) across turns.
    /// </summary>
    /// <typeparam name="TState">
    /// Caller-defined state type. Must be JSON-serializable (System.Text.Json) so that
    /// it can be checkpointed. Recommend using an immutable <c>record</c>.
    /// </typeparam>
    /// <param name="session">The agent session to invoke on each turn.</param>
    /// <param name="initialState">The initial value of <typeparamref name="TState"/>.</param>
    /// <param name="initialMessages">The messages to send on the first turn.</param>
    /// <param name="reducer">
    /// A pure function that merges the current state with the turn result to produce
    /// the next state. Called after every turn, before termination is evaluated.
    /// </param>
    /// <param name="termination">Condition checked after each turn.</param>
    /// <param name="maxTurns">Hard upper bound on turns. Defaults to 20.</param>
    /// <param name="onTurnCompleted">
    /// Optional callback invoked after the reducer runs, before termination is checked.
    /// Receives the updated state, the raw turn result, and the current turn number.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// An <see cref="AgentLoopResult{TState}"/> containing all turn results, the final
    /// result, the final state, the termination reason, and the number of turns completed.
    /// </returns>
    public static async ValueTask<AgentLoopResult<TState>> RunAsync<TState>(
        IAgentSession session,
        TState initialState,
        IReadOnlyList<AgentMessage> initialMessages,
        Func<TState, AgentTurnResult, TState> reducer,
        ITerminationCondition termination,
        int maxTurns = 20,
        Action<TState, AgentTurnResult, int>? onTurnCompleted = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(initialMessages);
        ArgumentNullException.ThrowIfNull(reducer);
        ArgumentNullException.ThrowIfNull(termination);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTurns, 1);

        // Reset stateful conditions before each run.
        termination.Reset();

        var allResults = new List<AgentTurnResult>();
        var currentMessages = initialMessages;
        var state = initialState;
        AgentLoopTerminationReason reason = AgentLoopTerminationReason.MaxTurnsReached;

        for (int turn = 1; turn <= maxTurns; turn++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await session.InvokeAsync(currentMessages, cancellationToken)
                .ConfigureAwait(false);

            allResults.Add(result);

            // Apply reducer: current state + turn result → next state.
            state = reducer(state, result);

            onTurnCompleted?.Invoke(state, result, turn);

            if (termination.ShouldTerminate(result, turn))
            {
                reason = AgentLoopTerminationReason.ConditionMet;
                return new AgentLoopResult<TState>(allResults, result, state, reason, turn);
            }

            // Feed the agent's output back as input for the next turn.
            currentMessages = result.Output;
        }

        return new AgentLoopResult<TState>(
            allResults,
            allResults[^1],
            state,
            reason,
            maxTurns);
    }
}

// ── Result types ──────────────────────────────────────────────────────────────

/// <summary>
/// Why the <see cref="AgentLoop"/> stopped.
/// </summary>
public enum AgentLoopTerminationReason
{
    /// <summary>The termination condition returned true.</summary>
    ConditionMet,

    /// <summary>The loop ran for <c>maxTurns</c> without the condition firing.</summary>
    MaxTurnsReached,
}

/// <summary>
/// The full output of a completed <see cref="AgentLoop.RunAsync"/> call.
/// </summary>
/// <param name="AllResults">Every turn result in order.</param>
/// <param name="FinalResult">The last turn result.</param>
/// <param name="Reason">Why the loop terminated.</param>
/// <param name="TurnsCompleted">Total number of turns executed.</param>
public sealed record AgentLoopResult(
    IReadOnlyList<AgentTurnResult> AllResults,
    AgentTurnResult FinalResult,
    AgentLoopTerminationReason Reason,
    int TurnsCompleted);

/// <summary>
/// The full output of a completed <see cref="AgentLoop.RunAsync{TState}"/> call.
/// Extends <see cref="AgentLoopResult"/> with the final value of <typeparamref name="TState"/>
/// after all reducer applications.
/// </summary>
/// <typeparam name="TState">The caller-defined state type.</typeparam>
/// <param name="AllResults">Every turn result in order.</param>
/// <param name="FinalResult">The last turn result.</param>
/// <param name="FinalState">The state produced by the last reducer invocation.</param>
/// <param name="Reason">Why the loop terminated.</param>
/// <param name="TurnsCompleted">Total number of turns executed.</param>
public sealed record AgentLoopResult<TState>(
    IReadOnlyList<AgentTurnResult> AllResults,
    AgentTurnResult FinalResult,
    TState FinalState,
    AgentLoopTerminationReason Reason,
    int TurnsCompleted);
