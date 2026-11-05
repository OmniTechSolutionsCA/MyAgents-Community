namespace MyAgents.Core.Execution;

/// <summary>Optional per-scope admission and execution limits applied atomically by a run backend.</summary>
public sealed record AgentRunBackendOptions
{
    /// <summary>Maximum nonterminal runs accepted for one scope; null means unlimited.</summary>
    public int? MaxOutstandingRunsPerScope { get; init; }

    /// <summary>Maximum simultaneously leased runs for one scope; null means unlimited.</summary>
    public int? MaxLeasedRunsPerScope { get; init; }

    /// <summary>Validates configured limits.</summary>
    public void Validate()
    {
        if (MaxOutstandingRunsPerScope is { } outstanding)
            ArgumentOutOfRangeException.ThrowIfLessThan(outstanding, 1);
        if (MaxLeasedRunsPerScope is { } leased)
            ArgumentOutOfRangeException.ThrowIfLessThan(leased, 1);
    }
}
