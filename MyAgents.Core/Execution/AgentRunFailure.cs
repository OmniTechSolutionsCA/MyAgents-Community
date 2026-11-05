namespace MyAgents.Core.Execution;

/// <summary>A safe, serializable description of a failed run attempt.</summary>
/// <param name="Code">Stable failure category suitable for filtering.</param>
/// <param name="Message">Human-readable failure summary. Avoid secrets and sensitive payloads.</param>
/// <param name="Retryable">Whether the worker should schedule another attempt when attempts remain.</param>
public sealed record AgentRunFailure(string Code, string Message, bool Retryable = true);
