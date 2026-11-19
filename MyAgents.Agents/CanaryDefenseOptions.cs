using MyAgents.Core.Security;

namespace MyAgents.Agents;

/// <summary>
/// Tuning for <see cref="CanaryDefenseFeature"/>. Detection and observability — how a violation is reacted to is
/// decided by <see cref="ICanaryViolationHandler"/>.
/// </summary>
public sealed class CanaryDefenseOptions
{
    /// <summary>
    /// Optional audit sink that receives a record for every egress finding.
    /// When <see langword="null"/> (the default), findings are not recorded to an audit sink.
    /// </summary>
    public IEgressAuditSink? AuditSink { get; set; }
    /// <summary>
    /// Payloads larger than this many UTF-8 bytes are rejected without being scanned.
    /// Default: 1 MiB. A payload over the limit fails closed rather than being truncated, because a
    /// truncated scan would let a canary sit just past the cut and never be examined.
    /// </summary>
    public int MaxScanBytes { get; set; } = 1_048_576;

    /// <summary>
    /// When <see langword="true"/>, a scanner that throws is ignored and the payload is treated as
    /// clean. Default <see langword="false"/>: a detector that cannot run is not a detector that
    /// passed, so a scanner failure blocks.
    /// </summary>
    public bool FailOpenOnScannerError { get; set; }

    /// <summary>
    /// Hosts that may legitimately appear in a markdown image URL (for example a CDN your UI
    /// renders from). An image pointing anywhere else is reported as
    /// <see cref="MyAgents.Core.Security.EgressSeverity.Suspicious"/>. Empty by default, which means
    /// every markdown image is flagged.
    /// </summary>
    public IReadOnlyCollection<string> BeaconHostAllowList { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Whether tool-call arguments are scanned before the tool runs. Default <see langword="true"/>.
    /// </summary>
    public bool ScanToolArguments { get; set; } = true;
}
