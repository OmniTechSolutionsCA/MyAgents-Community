namespace MyAgents.Core;

/// <summary>
/// Factory for creating agent sessions from definitions.
/// </summary>
public interface IAgentSessionFactory
{
    /// <summary>
    /// Creates a new session for the given definition.
    /// </summary>
    /// <param name="sessionId">Unique identifier for the session.</param>
    /// <param name="definition">The agent definition.</param>
    /// <returns>An active session ready to process turns.</returns>
    IAgentSession CreateSession(string sessionId, AgentDefinition definition);
}
