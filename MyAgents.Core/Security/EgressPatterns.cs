using System.Text.RegularExpressions;

namespace MyAgents.Core.Security;

/// <summary>
/// Compiled patterns used by <see cref="DefaultEgressScanner"/>. Source-generated so they stay
/// NativeAOT-safe and allocation-light.
/// </summary>
internal static partial class EgressPatterns
{
    /// <summary>
    /// Matches a markdown image or link and captures its target. <c>NonBacktracking</c> is used so a
    /// hostile 1 MiB payload cannot induce catastrophic backtracking; the pattern contains no
    /// constructs that require backtracking.
    /// </summary>
    [GeneratedRegex(
        @"(?<image>!?)\[[^\]\r\n]{0,512}\]\(\s*(?<url>[^)\s]{0,2048})",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    public static partial Regex MarkdownTarget();
}
