namespace MyAgents.Core;

/// <summary>
/// Declares a named capability that an agent supports.
/// Capabilities are informational — they allow orchestrators, routers, and tooling to
/// discover what an agent can do without inspecting its feature list directly.
/// </summary>
public interface IAgentCapability
{
    /// <summary>Unique identifier for this capability (e.g., "streaming", "tools", "memory").</summary>
    string Id { get; }

    /// <summary>Human-readable description of what this capability provides.</summary>
    string Description { get; }
}
