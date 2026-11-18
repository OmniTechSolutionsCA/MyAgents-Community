namespace MyAgents.Core.Security;

/// <summary>
/// Confidence of an <see cref="EgressFinding"/>. Ordered, so a scan result's overall severity is
/// the maximum over its findings.
/// </summary>
public enum EgressSeverity
{
    /// <summary>Recorded for telemetry only. Never changes the outcome of a turn.</summary>
    Informational = 0,

    /// <summary>
    /// A heuristic fired (markdown beacon, high-entropy URL, configured secret pattern). May be a
    /// false positive, so the caller decides whether it blocks.
    /// </summary>
    Suspicious = 1,

    /// <summary>
    /// A session canary was found. Canaries carry no legitimate reason to appear in output or in a
    /// tool argument, so this is treated as a confirmed violation and always fails closed.
    /// </summary>
    Confirmed = 2,
}
