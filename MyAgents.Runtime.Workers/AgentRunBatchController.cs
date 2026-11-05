using MyAgents.Core.Execution;

namespace MyAgents.Runtime.Workers;

/// <summary>Applies cancellation to every nonterminal run in a batch.</summary>
public sealed class AgentRunBatchController
{
    private readonly IAgentRunBatchController _controller;

    /// <summary>Creates a controller using the configured run backend.</summary>
    public AgentRunBatchController(IAgentRunBatchController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    /// <summary>Cancels a batch and returns the number of runs affected.</summary>
    public ValueTask<int> CancelAsync(string batchId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        return _controller.CancelBatchAsync(batchId, cancellationToken);
    }
}
