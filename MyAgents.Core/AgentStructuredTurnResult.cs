namespace MyAgents.Core;

/// <summary>
/// Strongly-typed structured result returned from an agent turn.
/// </summary>
/// <typeparam name=\"T\">The parsed structured response type.</typeparam>
/// <param name=\"Value\">The deserialized response value.</param>
/// <param name=\"TurnResult\">The underlying turn execution result, including telemetry and output messages.</param>
public sealed record AgentStructuredTurnResult<T>(
    T Value,
    AgentTurnResult TurnResult);
