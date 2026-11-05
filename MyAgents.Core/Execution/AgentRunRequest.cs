namespace MyAgents.Core.Execution;

/// <summary>Provider-neutral input for one logical unit of agent work.</summary>
/// <param name="HandlerId">Stable identifier resolved by the worker's handler registry.</param>
/// <param name="InputJson">Versioned JSON payload interpreted by the registered handler.</param>
/// <param name="IdempotencyKey">Optional key that deduplicates submissions within <paramref name="ScopeKey"/>.</param>
/// <param name="ScopeKey">Optional opaque partition key supplied by the application.</param>
/// <param name="Deadline">Optional UTC deadline after which the run cannot be claimed or completed.</param>
/// <param name="MaxAttempts">Maximum handler failures before the run becomes terminal.</param>
/// <param name="BatchId">Optional identifier grouping this run with a batch of related cases.</param>
/// <param name="CaseId">Optional stable identifier for this run's case within a batch.</param>
public sealed record AgentRunRequest(
    string HandlerId,
    string InputJson,
    string? IdempotencyKey = null,
    string? ScopeKey = null,
    DateTimeOffset? Deadline = null,
    int MaxAttempts = 3,
    string? BatchId = null,
    string? CaseId = null);
