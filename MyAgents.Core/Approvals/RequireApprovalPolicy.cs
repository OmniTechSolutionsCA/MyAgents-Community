namespace MyAgents.Core;

/// <summary>
/// Approval policy that always requires explicit approval before tool execution.
/// </summary>
public sealed class RequireApprovalPolicy : IToolApprovalPolicy
{
    private readonly string _reason;

    public RequireApprovalPolicy(string reason = "Manual approval required by policy.")
    {
        _reason = reason;
    }

    /// <inheritdoc />
    public ValueTask<ToolApprovalDecision> DecideAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ToolApprovalDecision.Pending(_reason));
    }
}
