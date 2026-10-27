using MyAgents.Core;

namespace MyAgents.Agents;

/// <summary>
/// Feature that adds <see cref="ContextWindowMiddleware"/> to the agent pipeline.
/// Trims the incoming message history on every turn so the inner pipeline never
/// sees more messages than the configured window allows.
/// </summary>
/// <remarks>
/// Compose this feature early in the features list — it should sit before
/// <see cref="TelemetryFeature"/> and <see cref="LoggingFeature"/> so all
/// downstream middleware work with the already-trimmed history.
/// </remarks>
/// <example>
/// <code>
/// var agent = new AgentDefinition(
///     Name: "MyAgent",
///     Features: new IAgentFeature[]
///     {
///         new ContextWindowFeature(maxMessages: 20),
///         new TelemetryFeature(),
///         new LoggingFeature(),
///     });
/// </code>
/// </example>
public sealed class ContextWindowFeature : IAgentFeature
{
    private readonly IContextWindowTrimmer _trimmer;

    /// <param name="maxMessages">
    /// Maximum number of non-system messages to retain per turn. Defaults to 20.
    /// System messages are always preserved. This overload uses a simple
    /// sliding window strategy; for token-based trimming use
    /// <see cref="WithTokenBudget(int, ITokenCounter?)"/> instead.
    /// </param>
    public ContextWindowFeature(int maxMessages = 20)
        : this(new SlidingWindowTrimmer(maxMessages)) { }

    /// <param name="trimmer">
    /// Custom trimmer implementation. Use this overload when you need
    /// token-based trimming or summarisation logic beyond simple sliding window.
    /// </param>
    public ContextWindowFeature(IContextWindowTrimmer trimmer)
    {
        ArgumentNullException.ThrowIfNull(trimmer);
        _trimmer = trimmer;
    }

    public void Contribute(AgentAssembly assembly)
        => assembly.Pipeline.Use(new ContextWindowMiddleware(_trimmer));

    /// <summary>
    /// Creates a <see cref="ContextWindowFeature"/> using a token-budget based
    /// trimming strategy.
    /// </summary>
    /// <param name="maxTokens">
    /// Maximum total token budget for all messages (system messages count toward
    /// the budget and are never removed). Must be at least 1.
    /// </param>
    /// <param name="tokenCounter">
    /// Optional token counter implementation; if <c>null</c> a
    /// <see cref="CharacterTokenCounter"/> (~4 chars/token) is used.
    /// </param>
    /// <returns>A new feature configured with a <see cref="TokenBudgetTrimmer"/>.</returns>
    public static ContextWindowFeature WithTokenBudget(int maxTokens, ITokenCounter? tokenCounter = null)
        => new ContextWindowFeature(new TokenBudgetTrimmer(maxTokens, tokenCounter));
}
