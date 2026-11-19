using System.Text.RegularExpressions;

namespace MyAgents.Core.Security;

/// <summary>
/// Default heuristic scanner: markdown beacons and high-entropy URLs.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrow. Markdown <b>images</b> are auto-fetched by most chat surfaces, so an image
/// URL pointing anywhere outside the allow-list is a silent one-way channel; plain links require a
/// click and are only flagged when their query string looks like a payload. Flagging every external
/// link would fire on ordinary citations and train operators to ignore the signal.
/// </para>
/// <para>
/// Stateless after construction and therefore safe to share across sessions.
/// </para>
/// </remarks>
internal sealed class DefaultEgressScanner : IEgressScanner
{
    private const int EntropySampleLength = 2048;
    private const int HighEntropyMinimumLength = 20;
    private const double HighEntropyThreshold = 4.0;

    private readonly HashSet<string> _beaconHostAllowList;
    private readonly SecretPattern[] _secretPatterns;

    public DefaultEgressScanner(
        IEnumerable<string>? beaconHostAllowList,
        IEnumerable<SecretPattern>? secretPatterns = null)
    {
        _beaconHostAllowList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (beaconHostAllowList is not null)
        {
            foreach (var host in beaconHostAllowList)
            {
                if (!string.IsNullOrWhiteSpace(host))
                    _beaconHostAllowList.Add(host);
            }
        }

        _secretPatterns = secretPatterns is null
            ? []
            : secretPatterns.Where(static p => p is not null).ToArray();
    }

    public ValueTask<EgressScanResult> ScanAsync(
        EgressChannel channel,
        string payload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(payload))
            return ValueTask.FromResult(EgressScanResult.Clean);

        List<EgressFinding>? findings = null;

        foreach (Match match in EgressPatterns.MarkdownTarget().Matches(payload))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isImage = match.Groups["image"].Length > 0;
            var urlValue = match.Groups["url"].Value;

            if (!Uri.TryCreate(urlValue, UriKind.Absolute, out var uri))
                continue;

            if (isImage && !_beaconHostAllowList.Contains(uri.Host))
            {
                (findings ??= []).Add(new EgressFinding(
                    DetectorName: "markdown-image-beacon",
                    Severity: EgressSeverity.Suspicious,
                    Reason: $"Markdown image points at non-allow-listed host '{uri.Host}'."));
                continue;
            }

            if (HasHighEntropyQuery(uri))
            {
                (findings ??= []).Add(new EgressFinding(
                    DetectorName: "high-entropy-url",
                    Severity: EgressSeverity.Suspicious,
                    Reason: $"URL carries a high-entropy query string (host '{uri.Host}')."));
            }
        }

        foreach (var secretPattern in _secretPatterns)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (secretPattern.Pattern.IsMatch(payload))
            {
                (findings ??= []).Add(new EgressFinding(
                    DetectorName: secretPattern.Name,
                    Severity: EgressSeverity.Suspicious,
                    Reason: $"Payload matched configured secret pattern '{secretPattern.Name}'."));
            }
        }

        return ValueTask.FromResult(findings is null ? EgressScanResult.Clean : new EgressScanResult(findings));
    }

    private static bool HasHighEntropyQuery(Uri uri)
    {
        var query = uri.Query;
        if (query.Length < HighEntropyMinimumLength)
            return false;

        var sample = query.Length > EntropySampleLength ? query[..EntropySampleLength] : query;
        return ShannonEntropy(sample) >= HighEntropyThreshold;
    }

    /// <summary>Bits per character over the sample's own alphabet.</summary>
    private static double ShannonEntropy(string value)
    {
        Span<int> counts = stackalloc int[128];
        var considered = 0;

        foreach (var character in value)
        {
            if (character >= 128)
            {
                considered++;
                continue;
            }

            counts[character]++;
            considered++;
        }

        if (considered == 0)
            return 0;

        var entropy = 0d;
        foreach (var count in counts)
        {
            if (count == 0)
                continue;

            var probability = (double)count / considered;
            entropy -= probability * Math.Log2(probability);
        }

        return entropy;
    }
}
