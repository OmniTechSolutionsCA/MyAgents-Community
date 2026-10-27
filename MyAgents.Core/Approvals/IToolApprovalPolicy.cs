namespace MyAgents.Core;

/// <summary>
/// Evaluates whether a tool invocation may proceed immediately or needs approval.
/// </summary>
public interface IToolApprovalPolicy
{
    /// <summary>
    /// Returns the policy decision for the supplied tool invocation request.
    /// </summary>
    ValueTask<ToolApprovalDecision> DecideAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default);
}
