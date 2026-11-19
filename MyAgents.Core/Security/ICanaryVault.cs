namespace MyAgents.Core.Security;

/// <summary>
/// Holds the canary tokens issued for one session and whether that session has been quarantined.
/// </summary>
/// <remarks>
/// <para>
/// A canary is a high-entropy token that has no legitimate reason to appear in agent output or in a
/// tool argument. Because it is meaningless outside the framework, a match is a confirmed
/// exfiltration attempt rather than a heuristic.
/// </para>
/// <para>
/// Quarantine is <b>monotonic</b>: once a session is quarantined, nothing can clear it. A turn that
/// caused a quarantine has already had its conversation poisoned, and leaving the session open
/// would let an attacker probe the detectors turn by turn.
/// </para>
/// </remarks>
public interface ICanaryVault
{
    /// <summary>The session these canaries belong to.</summary>
    string SessionId { get; }

    /// <summary>The canary tokens issued for this session. Empty when defense is not configured.</summary>
    IReadOnlyList<string> Canaries { get; }

    /// <summary>Whether this session has been quarantined. Never returns to <see langword="false"/>.</summary>
    bool IsQuarantined { get; }

    /// <summary>Why the session was quarantined, or <see langword="null"/> when it has not been.</summary>
    string? QuarantineReason { get; }

    /// <summary>
    /// Quarantines the session. Idempotent and safe to call concurrently: the first call wins and
    /// later calls do not overwrite <see cref="QuarantineReason"/>.
    /// </summary>
    /// <param name="reason">Why the session is being quarantined.</param>
    void Quarantine(string reason);
}
