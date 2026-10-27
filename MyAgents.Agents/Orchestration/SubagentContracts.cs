using MyAgents.Core;

namespace MyAgents.Agents;

/// <summary>
/// Lifecycle status of a subagent execution task.
/// </summary>
public enum SubagentExecutionState
{
    Queued,
    Running,
    Completed,
    Failed,
    Canceled,
}

/// <summary>
/// Task definition dispatched to a specialized subagent.
/// </summary>
public sealed record SubagentTask(
    string TaskId,
    string TargetAgentName,
    string Instructions,
    string? ParentSessionId = null,
    IReadOnlyList<AgentMessage>? InitialContext = null,
    int CurrentDepth = 1,
    TimeSpan? Timeout = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

/// <summary>
/// The final execution outcome and distilled output of a subagent turn or multi-turn execution.
/// </summary>
public sealed record SubagentResult(
    string TaskId,
    string TargetAgentName,
    SubagentExecutionState Status,
    string? FinalOutput,
    IReadOnlyList<AgentMessage> OutputMessages,
    TimeSpan Duration,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    int? TotalTokens = null,
    string? ErrorMessage = null);

/// <summary>
/// Handle representing an asynchronously forked subagent task running in the background.
/// </summary>
public sealed record SubagentTaskHandle(
    string TaskId,
    string TargetAgentName,
    SubagentExecutionState Status,
    DateTimeOffset CreatedAt);

/// <summary>
/// Exception thrown when a recursive subagent delegation exceeds the maximum allowable depth.
/// </summary>
public sealed class MaxDelegationDepthExceededException : InvalidOperationException
{
    public int CurrentDepth { get; }
    public int MaxDepth { get; }

    public MaxDelegationDepthExceededException(int currentDepth, int maxDepth)
        : base($"Subagent delegation depth limit exceeded. Current depth: {currentDepth}, maximum allowable: {maxDepth}.")
    {
        CurrentDepth = currentDepth;
        MaxDepth = maxDepth;
    }
}