using System.Security.Cryptography;

namespace MyAgents.Core.Security;

/// <summary>
/// Generates canary tokens drawn from a CSPRNG.
/// </summary>
/// <remarks>
/// The alphabet is RFC 4648 base32 (no padding), so a token survives being copied into a URL, a
/// JSON string, or a query parameter without percent-encoding, and contains no characters that a
/// model would "helpfully" normalise.
/// </remarks>
internal static class CanaryTokens
{
    private const string Prefix = "CANARY-";
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const int BodyLength = 26; // 26 * 5 bits = 130 bits of entropy

    /// <summary>Creates one canary token.</summary>
    public static string Create()
    {
        var token = new char[Prefix.Length + BodyLength];
        Prefix.AsSpan().CopyTo(token);

        for (var index = 0; index < BodyLength; index++)
            token[Prefix.Length + index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];

        return new string(token);
    }
}
