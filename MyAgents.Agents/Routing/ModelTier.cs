namespace MyAgents.Agents;

/// <summary>
/// Operational tier and cost class for a language model candidate.
/// </summary>
public enum ModelTier
{
    /// <summary>
    /// Fast, lightweight, or local small language models (SLMs) with lowest latency and cost.
    /// </summary>
    Economy,

    /// <summary>
    /// Balanced general-purpose models for standard reasoning and conversation.
    /// </summary>
    Standard,

    /// <summary>
    /// Frontier high-reasoning models for complex synthesis, code architecture, and formal verification.
    /// </summary>
    Advanced,
}