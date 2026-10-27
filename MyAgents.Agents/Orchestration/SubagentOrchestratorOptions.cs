namespace MyAgents.Agents;

/// <summary>
/// Configuration options for hierarchical subagent orchestration and handoff execution.
/// </summary>
public sealed class SubagentOrchestratorOptions
{
    /// <summary>
    /// Maximum allowable recursive delegation depth.
    /// Default is 3.
    /// </summary>
    public int MaxDelegationDepth { get; set; } = 3;

    /// <summary>
    /// Default execution timeout for subagent tasks if not specified at invocation time.
    /// Default is 5 minutes.
    /// </summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether subagent session IDs should be hierarchically prefixed with parent session identifiers.
    /// Default is true.
    /// </summary>
    public bool PrefixSubagentSessionId { get; set; } = true;
}