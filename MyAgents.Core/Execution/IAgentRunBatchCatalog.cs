namespace MyAgents.Core.Execution;

/// <summary>Queries persisted run outcomes grouped by their optional batch identifier.</summary>
public interface IAgentRunBatchCatalog
{
    /// <summary>Returns runs for a batch ordered by creation time and run identifier.</summary>
    ValueTask<IReadOnlyList<AgentRunRecord>> GetBatchRunsAsync(
        string batchId,
        CancellationToken cancellationToken = default);
}
