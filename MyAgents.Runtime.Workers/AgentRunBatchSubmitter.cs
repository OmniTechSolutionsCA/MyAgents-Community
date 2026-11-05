using System.Runtime.CompilerServices;
using MyAgents.Core.Execution;

namespace MyAgents.Runtime.Workers;

/// <summary>Streams case submissions into the shared run backend with batch correlation.</summary>
public sealed class AgentRunBatchSubmitter
{
    private readonly IAgentRunBackend _backend;

    /// <summary>Creates a batch submitter for the supplied run backend.</summary>
    public AgentRunBatchSubmitter(IAgentRunBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    /// <summary>
    /// Submits cases one at a time so an application can stream large input sets without buffering
    /// every submission. A failure can leave earlier cases submitted; callers can resume safely by
    /// using stable case IDs and request idempotency keys.
    /// </summary>
    public async IAsyncEnumerable<AgentRunSubmission> SubmitAsync(
        string batchId,
        IAsyncEnumerable<AgentRunBatchCase> cases,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentNullException.ThrowIfNull(cases);

        await foreach (var batchCase in cases.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            ArgumentNullException.ThrowIfNull(batchCase);
            ArgumentException.ThrowIfNullOrWhiteSpace(batchCase.CaseId);
            ArgumentNullException.ThrowIfNull(batchCase.Request);
            var request = batchCase.Request with { BatchId = batchId, CaseId = batchCase.CaseId };
            yield return await _backend.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
