using System.Collections.Concurrent;

namespace MyAgents.Core;

/// <summary>
/// In-memory approval storage for tests, samples, and local-only scenarios.
/// </summary>
public sealed class InMemoryToolApprovalStore : IToolApprovalStore, IToolApprovalInbox
{
    private readonly ConcurrentDictionary<string, PendingToolApproval> _approvals = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public ValueTask SaveAsync(PendingToolApproval approval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        cancellationToken.ThrowIfCancellationRequested();

        _approvals[approval.ApprovalId] = approval;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<PendingToolApproval?> GetAsync(string approvalId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        cancellationToken.ThrowIfCancellationRequested();

        _approvals.TryGetValue(approvalId, out var approval);
        return ValueTask.FromResult(approval);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PendingToolApproval>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        var approvals = _approvals.Values
            .Where(item => string.Equals(item.Request.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Request.RequestedAt)
            .ToArray();

        return ValueTask.FromResult<IReadOnlyList<PendingToolApproval>>(approvals);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PendingToolApproval>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var approvals = _approvals.Values
            .OrderByDescending(item => item.Request.RequestedAt)
            .ToArray();

        return ValueTask.FromResult<IReadOnlyList<PendingToolApproval>>(approvals);
    }
}
