namespace MyAgents.Core;

/// <summary>
/// Stable execution events emitted by runtimes, middleware, workflows, and tools.
/// </summary>
public enum RunEventKind
{
    TurnStarted,
    TurnCompleted,
    ToolRequested,
    ToolCompleted,
    SnapshotSaved,
    BranchCreated,
    RunResumed,
    ApprovalRequested,
    ApprovalDecided,
    PolicyStopped,
    GatewayMessageReceived,
    GatewayMessageSent,
    GatewayDeliveryCompleted,
    GatewayDeliveryFailed,
    GatewayDeliveryRetried,
    GatewayOwnershipRejected,
    GatewayRetryScheduled,
    GatewayProcessingFailed,
    CredentialRouteResolved,
    AgentHandoff,
    ExecutionOutput,
}
