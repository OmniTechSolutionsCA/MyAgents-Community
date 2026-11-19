using MyAgents.Core.Security;

namespace MyAgents.Agents;

/// <summary>
/// Quarantines the session and aborts when a canary is confirmed; leaves
/// <see cref="EgressSeverity.Suspicious"/> findings non-blocking so heuristics can be measured on
/// real traffic before they are trusted. This is the default handler.
/// </summary>
/// <remarks>
/// <para>
/// A confirmed canary has no legitimate reason to appear in output or in a tool argument, so the
/// conversation is already compromised by the time it is seen. Quarantine is monotonic: every
/// subsequent turn in that session is refused, which denies an attacker the turn-by-turn probing
/// that an open session would allow.
/// </para>
/// <para>
/// Suspicious findings deliberately do <b>not</b> abort. Markdown beacons and high-entropy URLs have
/// false positives; blocking on them by default would break legitimate turns before an operator has
/// had a chance to see what the rate looks like.
/// </para>
/// </remarks>
public sealed class QuarantineSessionViolationHandler : ICanaryViolationHandler
{
    private readonly ICanaryVault _vault;

    /// <summary>
    /// Creates a handler that quarantines <paramref name="vault"/> on a confirmed violation.
    /// </summary>
    /// <param name="vault">The session vault to quarantine.</param>
    public QuarantineSessionViolationHandler(ICanaryVault vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        _vault = vault;
    }

    /// <inheritdoc />
    public ValueTask<CanaryDisposition> HandleAsync(
        CanaryViolationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Result.Severity != EgressSeverity.Confirmed)
            return ValueTask.FromResult(CanaryDisposition.Allow);

        _vault.Quarantine($"{context.Result.Severity} egress violation on the {context.Channel} channel.");
        return ValueTask.FromResult(CanaryDisposition.Abort);
    }
}
