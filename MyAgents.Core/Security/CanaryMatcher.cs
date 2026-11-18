using System.Text;

namespace MyAgents.Core.Security;

/// <summary>
/// Matches session canaries against a payload, including simple obfuscations.
/// </summary>
/// <remarks>
/// An attacker who cannot paste a canary verbatim will encode it. Each pass below therefore
/// produces additional candidate strings from the payload and re-runs the exact match against them:
/// zero-width stripping defeats CANARY-splitting, Base64/hex/percent decoding defeats encoding, and
/// reversing defeats the cheapest of all transformations. This is best-effort, not a proof — see
/// R2 in the design notes: detection raises the cost of exfiltration, it does not close the channel.
/// </remarks>
internal static class CanaryMatcher
{
    private const int MinimumEncodedRunLength = 16;

    /// <summary>
    /// Returns the canary found in <paramref name="payload"/>, or <see langword="null"/> when clean.
    /// </summary>
    public static string? Match(IReadOnlyList<string> canaries, string payload)
    {
        if (canaries.Count == 0 || string.IsNullOrEmpty(payload))
            return null;

        foreach (var candidate in Candidates(payload))
        {
            for (var index = 0; index < canaries.Count; index++)
            {
                var canary = canaries[index];
                if (candidate.Contains(canary, StringComparison.Ordinal))
                    return canary;
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string payload)
    {
        yield return payload;

        var stripped = StripZeroWidth(payload);
        if (!string.Equals(stripped, payload, StringComparison.Ordinal))
            yield return stripped;

        var reversed = Reverse(payload);
        if (!string.Equals(reversed, payload, StringComparison.Ordinal))
            yield return reversed;

        var percentDecoded = DecodePercent(payload);
        if (percentDecoded is not null && !string.Equals(percentDecoded, payload, StringComparison.Ordinal))
            yield return percentDecoded;

        foreach (var decoded in DecodeEncodedRuns(StripZeroWidth(payload)))
            yield return decoded;
    }

    private static bool IsZeroWidth(char value)
        => value is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF';

    private static string StripZeroWidth(string value)
    {
        var hasAny = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (!IsZeroWidth(value[index]))
                continue;

            hasAny = true;
            break;
        }

        if (!hasAny)
            return value;

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (!IsZeroWidth(character))
                builder.Append(character);
        }

        return builder.ToString();
    }

    private static string Reverse(string value)
    {
        var characters = value.ToCharArray();
        Array.Reverse(characters);
        return new string(characters);
    }

    private static string? DecodePercent(string value)
    {
        if (!value.Contains('%', StringComparison.Ordinal))
            return null;

        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds runs of Base64 or hex and yields their decoded form. Runs shorter than
    /// <see cref="MinimumEncodedRunLength"/> are skipped: a 130-bit token cannot fit in a shorter
    /// encoding, and skipping avoids decoding every ordinary English word.
    /// </summary>
    private static IEnumerable<string> DecodeEncodedRuns(string payload)
    {
        var start = -1;

        for (var index = 0; index <= payload.Length; index++)
        {
            var isEncoded = index < payload.Length && IsEncodedCharacter(payload[index]);

            if (isEncoded)
            {
                if (start < 0)
                    start = index;

                continue;
            }

            if (start >= 0)
            {
                var length = index - start;
                if (length >= MinimumEncodedRunLength)
                {
                    var run = payload.AsSpan(start, length);
                    var decoded = TryDecodeBase64(run) ?? TryDecodeHex(run);
                    if (decoded is not null)
                        yield return decoded;
                }

                start = -1;
            }
        }
    }

    private static bool IsEncodedCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value is '+' or '/' or '=';

    private static string? TryDecodeBase64(ReadOnlySpan<char> run)
    {
        if (run.Length % 4 == 1)
            return null;

        var padded = run.Length % 4 == 0 ? run.Length : run.Length + (4 - (run.Length % 4));
        Span<char> buffer = padded <= 2048 ? stackalloc char[padded] : new char[padded];

        run.CopyTo(buffer);
        for (var index = run.Length; index < padded; index++)
            buffer[index] = '=';

        Span<byte> bytes = padded <= 2048 ? stackalloc byte[padded] : new byte[padded];
        return Convert.TryFromBase64Chars(buffer, bytes, out var written) && written > 0
            ? Encoding.UTF8.GetString(bytes[..written])
            : null;
    }

    private static string? TryDecodeHex(ReadOnlySpan<char> run)
    {
        if (run.Length % 2 != 0)
            return null;

        for (var index = 0; index < run.Length; index++)
        {
            if (!char.IsAsciiHexDigit(run[index]))
                return null;
        }

        try
        {
            var decoded = Convert.FromHexString(run);
            return decoded.Length > 0 ? Encoding.UTF8.GetString(decoded) : null;
        }
        catch (FormatException)
        {
            // Not hex after all; the run is simply not an encoding of anything we scan for.
            return null;
        }
    }
}
