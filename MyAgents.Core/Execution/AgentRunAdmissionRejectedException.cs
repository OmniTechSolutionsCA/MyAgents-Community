namespace MyAgents.Core.Execution;

/// <summary>Raised when a backend's configured per-scope outstanding-run limit is full.</summary>
public sealed class AgentRunAdmissionRejectedException : InvalidOperationException
{
    /// <summary>Scope whose configured outstanding-run limit rejected the submission.</summary>
    public string? ScopeKey { get; }

    /// <summary>Configured outstanding-run limit for the scope.</summary>
    public int Limit { get; }

    /// <summary>Creates an admission rejection for a full scope.</summary>
    public AgentRunAdmissionRejectedException(string? scopeKey, int limit)
        : base($"The outstanding run limit of {limit} was reached for scope '{scopeKey ?? "<default>"}'.")
    {
        ScopeKey = scopeKey;
        Limit = limit;
    }
}
