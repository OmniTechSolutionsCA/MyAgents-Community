namespace MyAgents.Core.Execution;

/// <summary>Result of submitting a run, including whether an idempotent submission was reused.</summary>
public sealed record AgentRunSubmission(string RunId, bool WasDeduplicated);
