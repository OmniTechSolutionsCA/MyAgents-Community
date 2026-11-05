namespace MyAgents.Core.Execution;

/// <summary>Applies lifecycle controls to all nonterminal runs in a batch.</summary>
public interface IAgentRunBatchController
{
    /// <summary>
    /// Cancels nonterminal runs in a batch and returns the number affected. Leased runs receive a
    /// cancellation request that their workers observe through lease renewal.
    /// </summary>
    ValueTask<int> CancelBatchAsync(
        string batchId,
        CancellationToken cancellationToken = default);
}
