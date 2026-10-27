namespace MyAgents.Core.Workflows;

/// <summary>
/// Persists and restores <see cref="WorkflowSnapshot"/> objects so that
/// long-running workflows can survive process restarts.
/// </summary>
/// <remarks>
/// Implementations are responsible for durability guarantees.
/// <see cref="FileSystemCheckpointer"/> provides a simple JSON-file-backed
/// implementation suitable for development and single-node deployments.
/// </remarks>
public interface IWorkflowCheckpointer
{
    /// <summary>
    /// Persists a snapshot. If a snapshot with the same
    /// <see cref="WorkflowSnapshot.SessionId"/> already exists it is overwritten.
    /// </summary>
    /// <param name="snapshot">The snapshot to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask SaveAsync(WorkflowSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the most recently saved snapshot for the given session identifier,
    /// or returns <c>null</c> if no snapshot exists.
    /// </summary>
    /// <param name="sessionId">The session identifier to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<WorkflowSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the snapshot for the given session identifier.
    /// No-op if the snapshot does not exist.
    /// </summary>
    /// <param name="sessionId">The session identifier whose snapshot should be deleted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask DeleteAsync(string sessionId, CancellationToken cancellationToken = default);
}
