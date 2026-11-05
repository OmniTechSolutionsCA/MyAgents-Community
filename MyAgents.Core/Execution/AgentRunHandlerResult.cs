namespace MyAgents.Core.Execution;

/// <summary>Result returned by a registered run handler.</summary>
public sealed record AgentRunHandlerResult(
    AgentRunHandlerDisposition Disposition,
    string? OutputJson = null,
    string? CheckpointJson = null,
    string? PendingInputDescription = null)
{
    /// <summary>Creates a successful terminal result.</summary>
    public static AgentRunHandlerResult Completed(string? outputJson = null)
        => new(AgentRunHandlerDisposition.Completed, OutputJson: outputJson);

    /// <summary>Pauses a run and stores the handler's resume checkpoint.</summary>
    public static AgentRunHandlerResult WaitingForInput(
        string checkpointJson,
        string pendingInputDescription)
    {
        ArgumentNullException.ThrowIfNull(checkpointJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingInputDescription);
        return new AgentRunHandlerResult(
            AgentRunHandlerDisposition.WaitingForInput,
            CheckpointJson: checkpointJson,
            PendingInputDescription: pendingInputDescription);
    }
}
