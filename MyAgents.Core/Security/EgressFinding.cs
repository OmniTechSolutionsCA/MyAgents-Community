namespace MyAgents.Core.Security;

/// <summary>
/// A single detector's contribution to an <see cref="EgressScanResult"/>.
/// </summary>
/// <param name="DetectorName">Stable identifier of the detector that fired, for telemetry and logs.</param>
/// <param name="Severity">The confidence of this finding.</param>
/// <param name="Reason">
/// Human-readable explanation for logs and audit sinks.
/// <b>Never</b> surface this to the model or to the caller of the agent: telling an attacker which
/// check tripped hands them an oracle they can probe until a payload gets through.
/// </param>
public sealed record EgressFinding(
    string DetectorName,
    EgressSeverity Severity,
    string Reason);
