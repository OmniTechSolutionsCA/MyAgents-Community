namespace MyAgents.Core;

/// <summary>
/// Default approval policy that allows tool execution immediately.
/// </summary>
public sealed class AllowAllToolApprovalPolicy : IToolApprovalPolicy
{
    /// <inheritdoc />
    public ValueTask<ToolApprovalDecision> DecideAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ToolApprovalDecision.Approve(reason: "Allowed by default policy."));
    }
}
