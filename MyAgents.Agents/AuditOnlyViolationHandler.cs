using MyAgents.Core.Security;

namespace MyAgents.Agents;

/// <summary>
/// Never blocks: records every finding to telemetry and lets the turn proceed. Intended for a
/// shadow-mode rollout, so trigger rates can be measured on real traffic before enforcement.
/// </summary>
/// <remarks>
/// <b>Do not use in production.</b> This handler allows confirmed canaries through as well as
/// heuristics — the whole point is to observe without enforcing. The default
/// <see cref="QuarantineSessionViolationHandler"/> is the one that fails closed.
/// </remarks>
public sealed class AuditOnlyViolationHandler : ICanaryViolationHandler
{
    /// <inheritdoc />
    public ValueTask<CanaryDisposition> HandleAsync(
        CanaryViolationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Findings were already recorded on the shared meter by the framework's scan step, so there
        // is nothing to add here; returning Allow is the entire behaviour.
        return ValueTask.FromResult(CanaryDisposition.Allow);
    }
}
