using System.Text.RegularExpressions;

namespace MyAgents.Core.Security;

/// <summary>
/// A named regular expression for opt-in secret detection in egress scanning.
/// </summary>
public sealed record SecretPattern
{
    /// <summary>
    /// Creates a new <see cref="SecretPattern"/> with the specified detector name and regular expression.
    /// </summary>
    /// <param name="name">Stable identifier reported in <see cref="EgressFinding.DetectorName"/> and <see cref="EgressFinding.Reason"/>.</param>
    /// <param name="pattern">Compiled or source-generated regular expression used to detect secret patterns in egress payloads.</param>
    public SecretPattern(string name, Regex pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(pattern);

        Name = name;
        Pattern = pattern;
    }

    /// <summary>
    /// Stable identifier of the secret pattern detector.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Regular expression evaluated against egress payloads.
    /// </summary>
    public Regex Pattern { get; }
}
