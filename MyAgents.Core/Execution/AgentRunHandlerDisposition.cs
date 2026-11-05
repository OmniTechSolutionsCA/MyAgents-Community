namespace MyAgents.Core.Execution;

/// <summary>How a run handler finished its current lease.</summary>
public enum AgentRunHandlerDisposition
{
    Completed,
    WaitingForInput,
}
