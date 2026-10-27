namespace MyAgents.Core.Workflows;

/// <summary>
/// A versioned, immutable point-in-time snapshot of the graph state captured
/// after a node completes execution. Used by the time-travel and branch-replay
/// features of <see cref="AgentGraph{TState}"/>.
/// </summary>
/// <param name="SnapshotId">Unique snapshot identifier (e.g. <c>"snap-{guid}"</c>).</param>
/// <param name="SessionId">The session this snapshot belongs to.</param>
/// <param name="WorkflowId">
/// Identifies the graph type (e.g. <c>"AgentGraph&lt;MyState&gt;"</c>).
/// </param>
/// <param name="NodeName">Name of the node that produced this snapshot.</param>
/// <param name="StateVersion">
/// Monotonically increasing version within the session. Starts at 1 and increments
/// after each node execution (including parallel fan-out branches).
/// </param>
/// <param name="StateJson">JSON-serialized graph state after this node completed.</param>
/// <param name="CreatedAt">UTC timestamp when this snapshot was created.</param>
/// <param name="ParentSnapshotId">
/// Snapshot ID of the immediately preceding snapshot in this session's execution chain.
/// <see langword="null"/> for the first snapshot in a session or branch.
/// </param>
/// <param name="BranchedFromSessionId">
/// Session ID of the original session from which this branched session was created via
/// <see cref="AgentGraph{TState}.ResumeFromSnapshotAsync"/>. <see langword="null"/> when
/// this is the root (un-branched) session.
/// </param>
public sealed record GraphSnapshot(
    string SnapshotId,
    string SessionId,
    string WorkflowId,
    string NodeName,
    int StateVersion,
    string StateJson,
    DateTimeOffset CreatedAt,
    string? ParentSnapshotId = null,
    string? BranchedFromSessionId = null)
{
    /// <summary>
    /// The next node a replay/resume should continue from when known.
    /// </summary>
    public string? ResumeFromNode { get; init; }

    /// <summary>
    /// Optional replay metadata describing which historical snapshot this state was replayed from.
    /// </summary>
    public string? ReplayedFromSnapshotId { get; init; }

    /// <summary>
    /// Optional branch label that can be surfaced in local studio views.
    /// </summary>
    public string? BranchLabel { get; init; }
}
