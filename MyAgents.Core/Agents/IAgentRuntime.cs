namespace MyAgents.Core;

/// <summary>
/// Runtime responsible for creating agents from definitions.
/// </summary>
public interface IAgentRuntime
{
    /// <summary>
    /// Creates an agent instance from the given definition and its pre-built assembly.
    /// The assembly is built once by the session factory and passed here so the runtime
    /// can access tools and system prompts without rebuilding them.
    /// </summary>
    /// <param name="definition">The agent definition (name, description).</param>
    /// <param name="assembly">The pre-built assembly containing tools, pipeline, and system prompt.</param>
    /// <returns>An agent instance ready for invocation.</returns>
    IAgent CreateAgent(AgentDefinition definition, AgentAssembly assembly);
}
