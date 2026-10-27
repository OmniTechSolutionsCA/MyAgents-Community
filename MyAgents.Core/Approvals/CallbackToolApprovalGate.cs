namespace MyAgents.Core;

/// <summary>
/// Delegates tool approval gate evaluation to a user-provided callback delegate.
/// </summary>
public sealed class CallbackToolApprovalGate : IToolApprovalGate
{
    private readonly Func<ToolApprovalRequest, ToolApprovalDecision, CancellationToken, ValueTask<ToolApprovalDecision>> _handler;

    public CallbackToolApprovalGate(
        Func<ToolApprovalRequest, ToolApprovalDecision, CancellationToken, ValueTask<ToolApprovalDecision>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    public CallbackToolApprovalGate(Func<ToolApprovalRequest, ToolApprovalDecision, ToolApprovalDecision> syncHandler)
    {
        ArgumentNullException.ThrowIfNull(syncHandler);
        _handler = (req, dec, _) => ValueTask.FromResult(syncHandler(req, dec));
    }

    public ValueTask<ToolApprovalDecision> EvaluateOrPromptAsync(
        ToolApprovalRequest request,
        ToolApprovalDecision pendingDecision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pendingDecision);
        cancellationToken.ThrowIfCancellationRequested();

        return _handler(request, pendingDecision, cancellationToken);
    }
}