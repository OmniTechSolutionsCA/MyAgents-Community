namespace MyAgents.Core;

/// <summary>
/// Result of an agent turn execution.
/// </summary>
/// <param name="Output">Messages output by the agent.</param>
/// <param name="Telemetry">Telemetry data captured during the turn.</param>
public sealed record AgentTurnResult(
    IReadOnlyList<AgentMessage> Output,
    AgentTurnTelemetry Telemetry);
