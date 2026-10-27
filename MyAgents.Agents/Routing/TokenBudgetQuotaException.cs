namespace MyAgents.Agents;

/// <summary>
/// Exception thrown when a turn or session token volume or dollar cost quota is breached.
/// </summary>
public sealed class TokenBudgetQuotaException : InvalidOperationException
{
    /// <summary>
    /// Identifier of the session that triggered the quota limit, if applicable.
    /// </summary>
    public string? SessionId { get; }

    /// <summary>
    /// Quota limit value configured for the breached constraint.
    /// </summary>
    public decimal? QuotaLimit { get; }

    /// <summary>
    /// Current usage value at the time the violation was detected.
    /// </summary>
    public decimal? CurrentUsage { get; }

    public TokenBudgetQuotaException(
        string message,
        string? sessionId = null,
        decimal? quotaLimit = null,
        decimal? currentUsage = null)
        : base(message)
    {
        SessionId = sessionId;
        QuotaLimit = quotaLimit;
        CurrentUsage = currentUsage;
    }
}