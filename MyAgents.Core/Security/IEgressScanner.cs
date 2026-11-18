namespace MyAgents.Core.Security;

/// <summary>
/// Pluggable heuristics applied to an outbound payload. Canary matching is <b>not</b> part of this
/// contract — canaries are session-scoped and are matched by the framework itself, so a scanner
/// never needs to see them.
/// </summary>
/// <remarks>
/// An implementation is registered once per agent definition and invoked from several sessions
/// concurrently, so it must be thread-safe and hold no per-session state.
/// </remarks>
public interface IEgressScanner
{
    /// <summary>
    /// Scans <paramref name="payload"/> for exfiltration heuristics.
    /// </summary>
    /// <param name="channel">Which outbound payload is being scanned.</param>
    /// <param name="payload">The text to scan.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The findings raised, or <see cref="EgressScanResult.Clean"/> when none.</returns>
    ValueTask<EgressScanResult> ScanAsync(
        EgressChannel channel,
        string payload,
        CancellationToken cancellationToken = default);
}
