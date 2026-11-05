using MyAgents.Core.Execution;

namespace MyAgents.Runtime.Workers;

/// <summary>One input case submitted as part of an agent run batch.</summary>
public sealed record AgentRunBatchCase(string CaseId, AgentRunRequest Request);
