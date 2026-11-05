namespace MyAgents.Core.Execution;

/// <summary>Result of renewing a worker lease.</summary>
public sealed record AgentRunLeaseRenewal(
    bool LeaseIsValid,
    bool CancellationRequested,
    DateTimeOffset? LeaseExpiresAt = null);
