namespace MyAgents.Core.Security;

/// <summary>
/// The outcome of scanning one outbound payload.
/// </summary>
/// <param name="Findings">Every finding raised, in detector order. Empty means the payload was clean.</param>
public sealed record EgressScanResult(IReadOnlyList<EgressFinding> Findings)
{
    /// <summary>
    /// A result with no findings. A scan of an empty payload yields this, never a violation.
    /// </summary>
    public static EgressScanResult Clean { get; } = new(Array.Empty<EgressFinding>());

    /// <summary>
    /// The highest severity across <see cref="Findings"/>, or
    /// <see cref="EgressSeverity.Informational"/> when there are none.
    /// </summary>
    public EgressSeverity Severity
        => Findings.Count == 0 ? EgressSeverity.Informational : Findings.Max(static finding => finding.Severity);

    /// <summary>
    /// <see langword="true"/> when at least one finding is <see cref="EgressSeverity.Confirmed"/>.
    /// A confirmed finding always fails closed, regardless of configured policy.
    /// </summary>
    public bool IsFlagged => Severity == EgressSeverity.Confirmed;
}
