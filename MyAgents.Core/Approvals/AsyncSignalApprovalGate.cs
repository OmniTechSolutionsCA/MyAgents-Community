using System.Collections.Concurrent;

namespace MyAgents.Core;

/// <summary>
/// Coordinates asynchronous out-of-band approvals (e.g. from webhooks, chat channels, or REST endpoints).
/// </summary>
public sealed class AsyncSignalApprovalGate : IToolApprovalGate
{
    private readonly ConcurrentDictionary<string, PendingEntry> _pending = new(StringComparer.Ordinal);
    private readonly TimeSpan _defaultTimeout;

    public AsyncSignalApprovalGate(TimeSpan? defaultTimeout = null)
    {
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>
    /// Event triggered when a tool approval request is waiting for a signal.
    /// </summary>
    public event Action<ToolApprovalRequest, ToolApprovalDecision>? OnApprovalRequested;

    /// <summary>
    /// Returns all currently pending approval requests waiting for an external signal.
    /// </summary>
    public IReadOnlyList<ToolApprovalRequest> PendingRequests =>
        _pending.Values.Select(p => p.Request).ToArray();

    public async ValueTask<ToolApprovalDecision> EvaluateOrPromptAsync(
        ToolApprovalRequest request,
        ToolApprovalDecision pendingDecision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pendingDecision);
        cancellationToken.ThrowIfCancellationRequested();

        var tcs = new TaskCompletionSource<ToolApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new PendingEntry(request, pendingDecision, tcs);
        _pending[request.ApprovalId] = entry;

        try
        {
            OnApprovalRequested?.Invoke(request, pendingDecision);

            using var timeoutCts = new CancellationTokenSource(_defaultTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            using (linkedCts.Token.Register(() => tcs.TrySetCanceled(linkedCts.Token)))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out waiting for external signal: return original pending decision so fallback handles it
            return pendingDecision;
        }
        finally
        {
            _pending.TryRemove(request.ApprovalId, out _);
        }
    }

    /// <summary>
    /// Submits a decision for a pending approval request.
    /// </summary>
    /// <returns>True if the request was found and resolved; false otherwise.</returns>
    public bool TrySubmitDecision(string approvalId, ToolApprovalDecision decision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        ArgumentNullException.ThrowIfNull(decision);

        if (_pending.TryGetValue(approvalId, out var entry))
        {
            return entry.Tcs.TrySetResult(decision);
        }

        return false;
    }

    private sealed record PendingEntry(
        ToolApprovalRequest Request,
        ToolApprovalDecision PendingDecision,
        TaskCompletionSource<ToolApprovalDecision> Tcs);
}