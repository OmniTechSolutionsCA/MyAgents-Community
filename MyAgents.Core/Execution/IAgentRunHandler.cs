namespace MyAgents.Core.Execution;

/// <summary>Executes one provider-neutral agent run payload.</summary>
public interface IAgentRunHandler
{
    /// <summary>Stable identifier matching <see cref="AgentRunRequest.HandlerId"/>.</summary>
    string HandlerId { get; }

    /// <summary>Executes or resumes a run under the supplied lease.</summary>
    ValueTask<AgentRunHandlerResult> ExecuteAsync(
        AgentRunContext context,
        CancellationToken cancellationToken = default);
}
