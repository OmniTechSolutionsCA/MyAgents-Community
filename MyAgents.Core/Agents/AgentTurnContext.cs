namespace MyAgents.Core;

/// <summary>
/// Context passed to an agent during a turn execution.
/// </summary>
/// <param name="SessionId">Unique identifier for the session.</param>
/// <param name="Messages">Messages for this turn.</param>
/// <param name="Services">Services available during the turn (DI container, logging, etc.).</param>
public sealed record AgentTurnContext(
    string SessionId,
    IReadOnlyList<AgentMessage> Messages,
    AgentTurnServices Services)
{
    /// <summary>
    /// Arbitrary per-turn capability bag that middleware and features can use to
    /// store contextual information during a single agent invocation. This is
    /// *not* related to the static agent capabilities declared on
    /// <see cref="AgentDefinition"/>; those live on <c>AgentSession.Capabilities</c>.
    ///
    /// The helper methods on <see cref="TurnCapabilities"/> provide a simple
    /// type-safe way to add and retrieve values.
    /// </summary>
    public TurnCapabilities Capabilities { get; } = new();
}
