namespace MyAgents.Core.Security;

/// <summary>
/// Structured audit record capturing an egress defense finding.
/// </summary>
/// <param name="AuditId">Unique identifier for this audit record.</param>
/// <param name="SessionId">The session in which the finding occurred.</param>
/// <param name="Channel">Which outbound payload channel tripped the detector.</param>
/// <param name="Severity">The confidence/severity of the finding.</param>
/// <param name="DetectorName">Stable identifier of the detector that fired.</param>
/// <param name="Reason">Human-readable explanation of why the detector fired.</param>
/// <param name="Timestamp">UTC timestamp when the record was created.</param>
/// <param name="ToolName">The name of the tool being called, when the channel is <see cref="EgressChannel.ToolArguments"/>.</param>
public sealed record EgressAuditRecord(
    string AuditId,
    string SessionId,
    EgressChannel Channel,
    EgressSeverity Severity,
    string DetectorName,
    string Reason,
    DateTimeOffset Timestamp,
    string? ToolName = null);
