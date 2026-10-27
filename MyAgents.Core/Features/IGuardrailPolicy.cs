namespace MyAgents.Core;

/// <summary>
/// A single, composable content-safety rule applied to the agent's output after
/// each turn. Multiple policies are evaluated in registration order.
/// </summary>
/// <remarks>
/// Policies are invoked synchronously against the composite output text only
/// when <see cref="EvaluateAsync"/> is called by the guardrail middleware.
/// Throw from <see cref="EvaluateAsync"/> to signal a hard error; return a
/// non-null <see cref="GuardrailViolation"/> to indicate a soft violation
/// that the middleware should handle according to the configured action.
/// </remarks>
public interface IGuardrailPolicy
{
    /// <summary>
    /// Evaluates the <paramref name="output"/> against this policy's rules.
    /// </summary>
    /// <param name="output">
    /// The agent's output messages for the current turn, in order.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="GuardrailViolation"/> describing the violation when the output
    /// breaks a rule, or <see langword="null"/> when the output is compliant.
    /// </returns>
    ValueTask<GuardrailViolation?> EvaluateAsync(
        IReadOnlyList<AgentMessage> output,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Describes a guardrail policy violation returned by <see cref="IGuardrailPolicy.EvaluateAsync"/>.
/// </summary>
/// <param name="PolicyName">Identifies which policy triggered the violation.</param>
/// <param name="Reason">Human-readable explanation of the violation.</param>
/// <param name="ReplacementMessage">
/// Optional substitute output to use instead of the violating response.
/// When <see langword="null"/> the guardrail middleware will use a generic fallback message.
/// </param>
public sealed record GuardrailViolation(
    string PolicyName,
    string Reason,
    string? ReplacementMessage = null);
