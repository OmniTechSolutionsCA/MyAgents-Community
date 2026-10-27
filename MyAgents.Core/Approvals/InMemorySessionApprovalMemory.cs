using System.Collections.Concurrent;

namespace MyAgents.Core;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="ISessionApprovalMemory"/>.
/// </summary>
public sealed class InMemorySessionApprovalMemory : ISessionApprovalMemory
{
    private readonly ConcurrentDictionary<string, SessionGrants> _sessions = new(StringComparer.Ordinal);

    public ValueTask RecordDecisionAsync(
        ToolApprovalRequest request,
        ToolApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decision);
        cancellationToken.ThrowIfCancellationRequested();

        if (decision.Status != ToolApprovalStatus.Approved || decision.Scope == ApprovalScope.Once)
            return ValueTask.CompletedTask;

        var grants = _sessions.GetOrAdd(request.SessionId, static _ => new SessionGrants());

        switch (decision.Scope)
        {
            case ApprovalScope.SessionAlways:
                grants.GlobalDecision = decision;
                break;

            case ApprovalScope.SessionTool:
                grants.ToolDecisions[request.ToolName] = decision;
                break;

            case ApprovalScope.SessionCall:
                var callKey = request.ToolName + "\n" + request.ArgumentsJson;
                grants.CallDecisions[callKey] = decision;
                break;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<ToolApprovalDecision?> TryGetGrantedDecisionAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_sessions.TryGetValue(request.SessionId, out var grants))
            return ValueTask.FromResult<ToolApprovalDecision?>(null);

        if (grants.GlobalDecision is not null)
            return ValueTask.FromResult<ToolApprovalDecision?>(grants.GlobalDecision);

        if (grants.ToolDecisions.TryGetValue(request.ToolName, out var toolDecision))
            return ValueTask.FromResult<ToolApprovalDecision?>(toolDecision);

        var callKey = request.ToolName + "\n" + request.ArgumentsJson;
        if (grants.CallDecisions.TryGetValue(callKey, out var callDecision))
            return ValueTask.FromResult<ToolApprovalDecision?>(callDecision);

        return ValueTask.FromResult<ToolApprovalDecision?>(null);
    }

    public ValueTask ClearSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        _sessions.TryRemove(sessionId, out _);
        return ValueTask.CompletedTask;
    }

    private sealed class SessionGrants
    {
        public volatile ToolApprovalDecision? GlobalDecision;
        public ConcurrentDictionary<string, ToolApprovalDecision> ToolDecisions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, ToolApprovalDecision> CallDecisions { get; } = new(StringComparer.Ordinal);
    }
}