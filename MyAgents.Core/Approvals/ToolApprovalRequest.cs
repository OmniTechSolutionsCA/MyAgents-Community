namespace MyAgents.Core;

/// <summary>
/// A request to run a tool invocation that may require human or policy approval.
/// </summary>
public sealed record ToolApprovalRequest(
    string ApprovalId,
    string SessionId,
    string ToolName,
    string ArgumentsJson,
    DateTimeOffset RequestedAt,
    string? CallId = null,
    string? WorkflowId = null,
    string? AgentName = null,
    string? Reason = null);
