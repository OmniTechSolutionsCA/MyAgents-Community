namespace MyAgents.Core;

/// <summary>
/// Interactively or asynchronously resolves pending tool approvals (e.g., human-in-the-loop CLI prompts, webhooks, or external signals).
/// </summary>
public interface IToolApprovalGate
{
    /// <summary>
    /// Evaluates or prompts for a decision when a tool approval is pending.
    /// </summary>
    /// <param name="request">The tool invocation approval request.</param>
    /// <param name="pendingDecision">The initial pending decision produced by policy evaluation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved decision (Approved, Rejected, or still Pending if deferred/timed out).</returns>
    ValueTask<ToolApprovalDecision> EvaluateOrPromptAsync(
        ToolApprovalRequest request,
        ToolApprovalDecision pendingDecision,
        CancellationToken cancellationToken = default);
}