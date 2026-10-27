namespace MyAgents.Core.Workflows;

/// <summary>
/// Stores a versioned history of <see cref="GraphSnapshot"/> records, enabling
/// time-travel and branch-replay operations on <see cref="AgentGraph{TState}"/> runs.
/// </summary>
/// <remarks>
/// Unlike <see cref="IWorkflowCheckpointer"/> — which stores only the latest checkpoint
/// per session — <see cref="IGraphHistoryStore"/> retains <em>every</em> node snapshot,
/// allowing callers to enumerate the full execution history and branch from any past state.
/// </remarks>
public interface IGraphHistoryStore
{
    /// <summary>
    /// Persists a new snapshot. Snapshot IDs must be globally unique.
    /// </summary>
    ValueTask SaveSnapshotAsync(GraphSnapshot snapshot, CancellationToken ct = default);

    /// <summary>
    /// Returns all snapshots for <paramref name="sessionId"/>, ordered by
    /// <see cref="GraphSnapshot.StateVersion"/> ascending.
    /// Returns an empty list when no snapshots exist for the session.
    /// </summary>
    ValueTask<IReadOnlyList<GraphSnapshot>> ListSnapshotsAsync(
        string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Returns the snapshot with the given <paramref name="snapshotId"/>,
    /// or <see langword="null"/> if not found.
    /// </summary>
    ValueTask<GraphSnapshot?> GetSnapshotAsync(
        string snapshotId, CancellationToken ct = default);
}
