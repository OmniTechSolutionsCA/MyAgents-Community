using System.Text;

namespace MyAgents.Core.Security;

/// <summary>
/// Runs the full egress check for one payload: size cap, canary match, then the pluggable scanner.
/// </summary>
/// <remarks>
/// Kept in one place so the turn middleware and the tool observer cannot drift apart in how they
/// decide a payload is a violation. Every failure mode is explicit and enumerated: an oversized
/// payload, a throwing scanner, and a canary hit all resolve to a finding rather than an exception,
/// so the caller applies the same disposition logic to all of them.
/// </remarks>
internal static class EgressGuard
{
    public static async ValueTask<EgressScanResult> ScanAsync(
        IReadOnlyList<string> canaries,
        IEgressScanner? scanner,
        EgressChannel channel,
        string payload,
        int maxScanBytes,
        bool failOpenOnScannerError,
        CancellationToken cancellationToken = default)
    {
        // An empty payload carries nothing to exfiltrate. It must not be treated as a failure:
        // an agent that legitimately returns no text would otherwise abort every turn.
        if (string.IsNullOrEmpty(payload))
            return EgressScanResult.Clean;

        // Checked before any scanning work so a multi-megabyte payload is rejected cheaply.
        // Truncating instead would let a canary sit just past the limit and never be examined.
        if (maxScanBytes > 0 && Encoding.UTF8.GetByteCount(payload) > maxScanBytes)
        {
            var oversize = new EgressScanResult(
            [
                new EgressFinding(
                    DetectorName: "payload-too-large",
                    Severity: EgressSeverity.Confirmed,
                    Reason: $"Payload exceeds the {maxScanBytes}-byte scan limit and was not scanned."),
            ]);

            Record(oversize, channel);
            return oversize;
        }

        List<EgressFinding>? findings = null;

        var matchedCanary = CanaryMatcher.Match(canaries, payload);
        if (matchedCanary is not null)
        {
            (findings ??= []).Add(new EgressFinding(
                DetectorName: "session-canary",
                Severity: EgressSeverity.Confirmed,
                Reason: $"Session canary found on the {channel} channel."));
        }
        else if (CanaryMatcher.MatchFragments(canaries, payload) is not null)
        {
            // The token is present but was not contiguous: it arrived in pieces with other text
            // between them, which is what a cross-turn split looks like.
            (findings ??= []).Add(new EgressFinding(
                DetectorName: "session-canary-fragment",
                Severity: EgressSeverity.Confirmed,
                Reason: $"Session canary found in fragments on the {channel} channel."));
        }

        if (scanner is not null)
        {
            EgressScanResult scanned;
            try
            {
                scanned = await scanner.ScanAsync(channel, payload, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not CanaryViolationException)
            {
                // Fail closed by default: a detector that cannot run is not a detector that passed.
                scanned = failOpenOnScannerError
                    ? new EgressScanResult(
                    [
                        new EgressFinding(
                            DetectorName: "scanner-error",
                            Severity: EgressSeverity.Informational,
                            Reason: $"Egress scanner threw and was ignored (fail-open): {exception.Message}"),
                    ])
                    : new EgressScanResult(
                    [
                        new EgressFinding(
                            DetectorName: "scanner-error",
                            Severity: EgressSeverity.Confirmed,
                            Reason: $"Egress scanner threw and the payload could not be cleared: {exception.Message}"),
                    ]);
            }

            if (scanned.Findings.Count > 0)
                (findings ??= []).AddRange(scanned.Findings);
        }

        var result = findings is null ? EgressScanResult.Clean : new EgressScanResult(findings);
        Record(result, channel);
        return result;
    }

    private static void Record(EgressScanResult result, EgressChannel channel)
    {
        foreach (var finding in result.Findings)
        {
            CanaryTelemetry.Violations.Add(
                1,
                new KeyValuePair<string, object?>("agent.egress.channel", channel.ToString()),
                new KeyValuePair<string, object?>("agent.egress.severity", finding.Severity.ToString()),
                new KeyValuePair<string, object?>("agent.egress.detector", finding.DetectorName));
        }
    }
}
