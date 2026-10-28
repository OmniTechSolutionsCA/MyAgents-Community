using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace MyAgents.Core.Workflows;

// ── Public node/edge types ─────────────────────────────────────────────────────

/// <summary>
/// A graph node: maps a node name to an agent invocation step.
/// The node receives the current state and a session for the named agent,
/// and returns the updated state after the agent has run.
/// </summary>
/// <typeparam name="TState">Caller-defined graph state type.</typeparam>
public delegate ValueTask<TState> GraphNodeDelegate<TState>(
    TState state,
    IAgentSession session,
    CancellationToken cancellationToken);

/// <summary>
/// Why an <see cref="AgentGraph{TState}"/> run ended.
/// </summary>
public enum GraphRunReason
{
    /// <summary>Execution reached the special END sentinel node.</summary>
    Completed,

    /// <summary>The maximum number of node transitions was exceeded.</summary>
    MaxIterationsReached,
}

/// <summary>
/// The full output of a completed <see cref="AgentGraph{TState}.RunAsync"/> call.
/// </summary>
/// <param name="FinalState">State after the last node executed.</param>
/// <param name="Reason">Why execution stopped.</param>
/// <param name="IterationsExecuted">Total node transitions performed.</param>
public sealed record GraphRunResult<TState>(
    TState FinalState,
    GraphRunReason Reason,
    int IterationsExecuted);

/// <summary>
/// An event yielded by <see cref="AgentGraph{TState}.RunStreamingAsync"/> as each graph
/// node (or parallel fan-out branch) completes.
/// </summary>
/// <typeparam name="TState">Caller-defined graph state type.</typeparam>
/// <param name="NodeName">
/// Name of the node that just completed, or <see cref="AgentGraph{TState}.End"/>
/// for the terminal sentinel event.
/// </param>
/// <param name="StateAfterNode">
/// Accumulated graph state after this node's output was merged.
/// </param>
/// <param name="IsFinal">
/// <see langword="true"/> for the one terminal event that ends the stream.
/// </param>
/// <param name="Reason">
/// Why the run ended. Only meaningful when <see cref="IsFinal"/> is
/// <see langword="true"/>.
/// </param>
/// <param name="NodeOutput">
/// Raw agent messages produced by this node, if available.
/// <see langword="null"/> for synthetic events (fan-out merges, END sentinel).
/// </param>
public sealed record GraphStreamEvent<TState>(
    string NodeName,
    TState StateAfterNode,
    bool IsFinal,
    GraphRunReason? Reason = null,
    IReadOnlyList<AgentMessage>? NodeOutput = null);

// ── AgentGraph<TState> ─────────────────────────────────────────────────────────

/// <summary>
/// A directed graph that routes control flow between named agent nodes based on
/// state transitions. Inspired by LangGraph's compile/run model but scoped to
/// .NET idioms and the MyAgents <see cref="IAgentSession"/> contract.
/// </summary>
/// <typeparam name="TState">
/// Caller-defined state type. Should be a record or otherwise immutable-by-convention
/// so that nodes return new instances rather than mutating shared state.
/// </typeparam>
/// <remarks>
/// <para><b>v2 capabilities</b>: sequential execution, conditional routing, parallel fan-out
/// via <see cref="AddFanOutEdge"/>, streaming execution via <see cref="RunStreamingAsync"/>,
/// time-travel / branch-replay via <see cref="ResumeFromSnapshotAsync"/> (requires an
/// <see cref="IGraphHistoryStore"/>), and max-iteration cycle guard.</para>
///
/// <para><b>Execution model</b>: primary nodes execute sequentially. After a node completes,
/// if a fan-out edge was registered via <see cref="AddFanOutEdge"/>, the listed parallel nodes
/// run concurrently and their states are merged in declaration order before routing continues.
/// The <c>merge</c> delegate is called after every node (sequential or fan-out) to fold the
/// node's returned state into the accumulated graph state.</para>
/// </remarks>


public sealed class AgentGraph<TState>
{
    // ── Reserved node names ────────────────────────────────────────────────────
    /// <summary>
    /// Special sentinel value. Route an edge to <see cref="End"/> to terminate
    /// graph execution normally.
    /// </summary>
    public const string End = "__END__";

    /// <summary>
    /// Conventional name for the first node executed. Not required — you may
    /// pass any registered node name as <c>startNode</c> to <see cref="RunAsync"/>.
    /// </summary>
    public const string Start = "__START__";

    // ── Internal graph structure ───────────────────────────────────────────────
    private readonly Dictionary<string, GraphNodeDelegate<TState>> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _edges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<TState, string>> _conditionalEdges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SendRouter<TState>> _sendEdges = new(StringComparer.Ordinal);
    private readonly IAgentSessionFactory _sessionFactory;
    private readonly AgentDefinition[] _agentDefinitions;
    private readonly Func<TState, TState, TState> _merge;
    private readonly int _maxIterations;
    private readonly IWorkflowCheckpointer? _checkpointer;
    private readonly Dictionary<string, string[]> _fanOutEdges = new(StringComparer.Ordinal);
    private readonly HashSet<string> _subgraphNodes = new(StringComparer.Ordinal);
    private readonly IGraphHistoryStore? _historyStore;

    /// <summary>
    /// Creates a new graph.
    /// </summary>
    /// <param name="sessionFactory">
    /// Used to open an <see cref="IAgentSession"/> for each node invocation.
    /// </param>
    /// <param name="agentDefinitions">
    /// All agent definitions that nodes in this graph may reference. Each definition's
    /// <see cref="AgentDefinition.Name"/> must match the agent name passed to
    /// <see cref="AddNode"/>.
    /// </param>
    /// <param name="merge">
    /// Called after every node to fold the node's output state into the accumulated
    /// graph state (first argument = current accumulated state, second = state returned
    /// by the node that just executed). Common implementations:
    /// <list type="bullet">
    ///   <item><description><c>(_, b) => b</c> — replace: the node's output becomes the new state.</description></item>
    ///   <item><description><c>(a, b) => a with { Field = b.Field }</c> — selective merge: carry one field forward.</description></item>
    /// </list>
    /// </param>
    /// <param name="maxIterations">
    /// Hard upper bound on node transitions before the run is aborted with
    /// <see cref="GraphRunReason.MaxIterationsReached"/>. Defaults to 100.
    /// </param>
    public AgentGraph(
        IAgentSessionFactory sessionFactory,
        IEnumerable<AgentDefinition> agentDefinitions,
        Func<TState, TState, TState> merge,
        int maxIterations = 100,
        IWorkflowCheckpointer? checkpointer = null,
        IGraphHistoryStore? historyStore = null)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _agentDefinitions = (agentDefinitions ?? throw new ArgumentNullException(nameof(agentDefinitions))).ToArray();
        _merge = merge ?? throw new ArgumentNullException(nameof(merge));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIterations, 1);
        _maxIterations = maxIterations;
        _checkpointer = checkpointer;
        _historyStore = historyStore;
    }

    // ── Graph construction ─────────────────────────────────────────────────────

    /// <summary>
    /// Registers a node with a custom delegate.
    /// </summary>
    /// <param name="nodeName">Unique node name. Use matching agent definition names for agent nodes.</param>
    /// <param name="node">The delegate that executes this node.</param>
    public AgentGraph<TState> AddNode(string nodeName, GraphNodeDelegate<TState> node)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        ArgumentNullException.ThrowIfNull(node);
        if (nodeName == End)
            throw new ArgumentException($"Cannot add a node named '{End}' — it is a reserved sentinel.", nameof(nodeName));
        _nodes[nodeName] = node;
        return this;
    }

    /// <summary>
    /// Adds an unconditional directed edge from <paramref name="fromNode"/> to
    /// <paramref name="toNode"/>. Use <see cref="End"/> as <paramref name="toNode"/>
    /// to terminate the graph after <paramref name="fromNode"/> executes.
    /// </summary>
    public AgentGraph<TState> AddEdge(string fromNode, string toNode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromNode);
        ArgumentException.ThrowIfNullOrWhiteSpace(toNode);
        if (_conditionalEdges.ContainsKey(fromNode))
            throw new InvalidOperationException($"Node '{fromNode}' already has a conditional edge. A node may have either a conditional or an unconditional edge, not both.");
        _edges[fromNode] = toNode;
        return this;
    }

    /// <summary>
    /// Adds a conditional edge from <paramref name="fromNode"/>.
    /// After <paramref name="fromNode"/> executes, <paramref name="router"/> is called
    /// with the current state and must return the name of the next node (or <see cref="End"/>).
    /// </summary>
    public AgentGraph<TState> AddConditionalEdge(string fromNode, Func<TState, string> router)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromNode);
        ArgumentNullException.ThrowIfNull(router);
        if (_edges.ContainsKey(fromNode))
            throw new InvalidOperationException($"Node '{fromNode}' already has an unconditional edge. A node may have either a conditional or an unconditional edge, not both.");
        _conditionalEdges[fromNode] = router;
        return this;
    }

    /// <summary>
    /// Adds a dynamic Send API edge from <paramref name="fromNode"/>.
    /// </summary>
    /// <remarks>
    /// After <paramref name="fromNode"/> executes, <paramref name="router"/> computes
    /// runtime branch targets. Each packet contains the target node and the exact state
    /// that branch should receive. Branches execute concurrently and are merged in packet
    /// order into the accumulated graph state.
    /// </remarks>
    /// <param name="fromNode">Node that emits dynamic parallel branches.</param>
    /// <param name="router">Runtime branch router.</param>
    public AgentGraph<TState> AddSendEdge(string fromNode, SendRouter<TState> router)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromNode);
        ArgumentNullException.ThrowIfNull(router);
        if (_sendEdges.ContainsKey(fromNode))
            throw new InvalidOperationException($"Node '{fromNode}' already has a Send edge.");
        _sendEdges[fromNode] = router;
        return this;
    }

    /// <summary>
    /// Adds a subgraph node that executes an inner graph and maps state in/out.
    /// </summary>
    /// <typeparam name="TInnerState">Inner graph state type.</typeparam>
    /// <param name="nodeName">Node name to register in the outer graph.</param>
    /// <param name="subgraph">Inner graph to execute when this node runs.</param>
    /// <param name="subgraphStartNode">Start node for the inner graph.</param>
    /// <param name="mapIn">Maps outer state to inner state.</param>
    /// <param name="mapOut">Merges inner final state back into outer state.</param>
    /// <param name="initialMessages">Optional initial messages for inner graph execution.</param>
    public AgentGraph<TState> AddSubgraph<TInnerState>(
        string nodeName,
        AgentGraph<TInnerState> subgraph,
        string subgraphStartNode,
        Func<TState, TInnerState> mapIn,
        Func<TState, TInnerState, TState> mapOut,
        IReadOnlyList<AgentMessage>? initialMessages = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        ArgumentNullException.ThrowIfNull(subgraph);
        ArgumentException.ThrowIfNullOrWhiteSpace(subgraphStartNode);
        ArgumentNullException.ThrowIfNull(mapIn);
        ArgumentNullException.ThrowIfNull(mapOut);

        AddNode(nodeName, async (state, _, ct) =>
        {
            var innerInitialState = mapIn(state);
            var innerResult = await subgraph.RunAsync(
                subgraphStartNode,
                innerInitialState,
                initialMessages ?? Array.Empty<AgentMessage>(),
                graphSessionId: $"subgraph-{nodeName}-{Guid.NewGuid():N}",
                cancellationToken: ct).ConfigureAwait(false);

            return mapOut(state, innerResult.FinalState);
        });

        _subgraphNodes.Add(nodeName);
        return this;
    }

    /// <summary>
    /// Adds a fan-out from <paramref name="fromNode"/> to two or more parallel nodes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// After <paramref name="fromNode"/> completes and its state is merged, all
    /// <paramref name="parallelNodes"/> execute concurrently via <see cref="Task.WhenAll"/>.
    /// Their states are merged in declaration order into the accumulated graph state.
    /// Once all branches finish, routing continues from <paramref name="fromNode"/>'s
    /// outgoing edge (set via <see cref="AddEdge"/> or <see cref="AddConditionalEdge"/>).
    /// </para>
    /// <para>
    /// Fan-out parallel nodes are <em>leaf nodes</em> in the fan-out subgraph — they supply
    /// results to the merge function but do not themselves determine the next sequential node.
    /// </para>
    /// </remarks>
    /// <param name="fromNode">
    /// The node after which parallel execution begins. Must be registered via
    /// <see cref="AddNode"/> before calling <see cref="RunAsync"/>.
    /// </param>
    /// <param name="parallelNodes">
    /// Two or more node names to execute concurrently. All must be registered via
    /// <see cref="AddNode"/> before calling <see cref="RunAsync"/>.
    /// </param>
    public AgentGraph<TState> AddFanOutEdge(string fromNode, params string[] parallelNodes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromNode);
        ArgumentNullException.ThrowIfNull(parallelNodes);
        if (parallelNodes.Length < 2)
            throw new ArgumentException("Fan-out requires at least two parallel nodes.", nameof(parallelNodes));
        if (_fanOutEdges.ContainsKey(fromNode))
            throw new InvalidOperationException($"Node '{fromNode}' already has a fan-out edge.");
        _fanOutEdges[fromNode] = parallelNodes;
        return this;
    }

    // ── Execution ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the graph starting at <paramref name="startNode"/> and returns the final state
    /// once execution reaches <see cref="End"/> or the iteration cap is exceeded.
    /// </summary>
    /// <param name="startNode">The name of the first node to execute.</param>
    /// <param name="initialState">The state to pass to the first node.</param>
    /// <param name="initialMessages">Messages provided to every node's first invocation.</param>
    /// <param name="graphSessionId">
    /// Optional stable ID for checkpointing and history. A random ID is generated when
    /// <see langword="null"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask<GraphRunResult<TState>> RunAsync(
        string startNode,
        TState initialState,
        IReadOnlyList<AgentMessage> initialMessages,
        string? graphSessionId = null,
        CancellationToken cancellationToken = default)
        => await RunAsyncCore(
            startNode,
            initialState,
            initialMessages,
            graphSessionId,
            branchedFromSessionId: null,
            replayedFromSnapshotId: null,
            branchLabel: null,
            cancellationToken).ConfigureAwait(false);

    private async ValueTask<GraphRunResult<TState>> RunAsyncCore(
        string startNode,
        TState initialState,
        IReadOnlyList<AgentMessage> initialMessages,
        string? graphSessionId,
        string? branchedFromSessionId,
        string? replayedFromSnapshotId,
        string? branchLabel,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startNode);
        ArgumentNullException.ThrowIfNull(initialMessages);

        ValidateGraph(startNode);

        var sessionId = graphSessionId ?? $"graph-{Guid.NewGuid():N}";
        var workflowId = $"AgentGraph<{typeof(TState).Name}>";

        var state = initialState;
        var currentNode = startNode;
        int iterations = 0;
        int stateVersion = 0;
        string? lastSnapshotId = null;

        while (currentNode != End)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (iterations >= _maxIterations)
                return new GraphRunResult<TState>(state, GraphRunReason.MaxIterationsReached, iterations);

            // Execute the primary node and merge its output into accumulated state
            var nodeOutput = await ExecuteNodeAsync(currentNode, state, cancellationToken).ConfigureAwait(false);
            state = _merge(state, nodeOutput);
            iterations++;
            stateVersion++;
            lastSnapshotId = await SaveSnapshotAsync(
                workflowId, sessionId, currentNode, stateVersion, state, lastSnapshotId,
                branchedFromSessionId, replayedFromSnapshotId, branchLabel, cancellationToken)
                .ConfigureAwait(false);

            // Fan-out: execute parallel branches concurrently
            if (_fanOutEdges.TryGetValue(currentNode, out var parallelNodes))
            {
                var fanOutTasks = parallelNodes
                    .Select(pNode => ExecuteNodeAsync(pNode, state, cancellationToken).AsTask())
                    .ToArray();
                var fanOutStates = await Task.WhenAll(fanOutTasks).ConfigureAwait(false);

                foreach (var (pNode, pState) in parallelNodes.Zip(fanOutStates))
                {
                    state = _merge(state, pState);
                    iterations++;
                    stateVersion++;
                    lastSnapshotId = await SaveSnapshotAsync(
                        workflowId, sessionId, pNode, stateVersion, state, lastSnapshotId,
                        branchedFromSessionId, replayedFromSnapshotId, branchLabel, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            // Dynamic Send API: runtime branch packets, each with branch-specific state
            if (_sendEdges.TryGetValue(currentNode, out var sendRouter))
            {
                var packets = sendRouter(state) ?? Array.Empty<SendPacket<TState>>();
                foreach (var packet in packets)
                {
                    if (!_nodes.ContainsKey(packet.NodeName))
                        throw new InvalidOperationException(
                            $"Send edge from '{currentNode}' routed to unregistered node '{packet.NodeName}'.");
                }

                var sendTasks = packets
                    .Select(packet => ExecuteNodeAsync(packet.NodeName, packet.State, cancellationToken).AsTask())
                    .ToArray();

                var sendStates = await Task.WhenAll(sendTasks).ConfigureAwait(false);
                foreach (var (packet, branchState) in packets.Zip(sendStates))
                {
                    state = _merge(state, branchState);
                    iterations++;
                    stateVersion++;
                    lastSnapshotId = await SaveSnapshotAsync(
                        workflowId, sessionId, packet.NodeName, stateVersion, state, lastSnapshotId,
                        branchedFromSessionId, replayedFromSnapshotId, branchLabel, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            // Rolling checkpoint: overwrites previous entry so the latest state is always recoverable
            if (_checkpointer is not null)
            {
                await _checkpointer.SaveAsync(new WorkflowSnapshot(
                    WorkflowId: workflowId,
                    StateVersion: stateVersion,
                    SessionId: sessionId,
                    StateJson: JsonSerializer.Serialize(state)), cancellationToken).ConfigureAwait(false);
            }

            // Determine the next node
            if (_conditionalEdges.TryGetValue(currentNode, out var router))
                currentNode = router(state);
            else if (_edges.TryGetValue(currentNode, out var nextNode))
                currentNode = nextNode;
            else
                break; // implicit END
        }

        return new GraphRunResult<TState>(state, GraphRunReason.Completed, iterations);
    }

    /// <summary>
    /// Streams graph execution, yielding a <see cref="GraphStreamEvent{TState}"/> as each
    /// node (including parallel fan-out branches) completes. The final event always has
    /// <see cref="GraphStreamEvent{TState}.IsFinal"/> = <see langword="true"/> and
    /// <see cref="GraphStreamEvent{TState}.NodeName"/> = <see cref="End"/>.
    /// </summary>
    /// <param name="startNode">The name of the first node to execute.</param>
    /// <param name="initialState">The state to pass to the first node.</param>
    /// <param name="initialMessages">Messages provided to every node's first invocation.</param>
    /// <param name="graphSessionId">
    /// Optional stable ID used for history and checkpointing. A random ID is generated when
    /// <see langword="null"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async IAsyncEnumerable<GraphStreamEvent<TState>> RunStreamingAsync(
        string startNode,
        TState initialState,
        IReadOnlyList<AgentMessage> initialMessages,
        string? graphSessionId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startNode);
        ArgumentNullException.ThrowIfNull(initialMessages);

        ValidateGraph(startNode);

        var sessionId = graphSessionId ?? $"graph-{Guid.NewGuid():N}";
        var workflowId = $"AgentGraph<{typeof(TState).Name}>";

        var state = initialState;
        var currentNode = startNode;
        int iterations = 0;
        int stateVersion = 0;
        string? lastSnapshotId = null;

        while (currentNode != End)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (iterations >= _maxIterations)
            {
                yield return new GraphStreamEvent<TState>(
                    currentNode, state, IsFinal: true,
                    Reason: GraphRunReason.MaxIterationsReached);
                yield break;
            }

            // Execute the primary node and merge its output
            var streamNodeOutput = await ExecuteNodeAsync(currentNode, state, cancellationToken).ConfigureAwait(false);
            state = _merge(state, streamNodeOutput);
            iterations++;
            stateVersion++;
            lastSnapshotId = await SaveSnapshotAsync(
                workflowId, sessionId, currentNode, stateVersion, state, lastSnapshotId,
                branchedFromSessionId: null, replayedFromSnapshotId: null, branchLabel: null, cancellationToken)
                .ConfigureAwait(false);

            // Yield intermediate event for the primary node (IsFinal decided after routing)
            yield return new GraphStreamEvent<TState>(currentNode, state, IsFinal: false);

            // Fan-out: execute parallel branches, yield one event per branch
            if (_fanOutEdges.TryGetValue(currentNode, out var parallelNodes))
            {
                var fanOutTasks = parallelNodes
                    .Select(pNode => ExecuteNodeAsync(pNode, state, cancellationToken).AsTask())
                    .ToArray();
                var fanOutStates = await Task.WhenAll(fanOutTasks).ConfigureAwait(false);

                foreach (var (pNode, pState) in parallelNodes.Zip(fanOutStates))
                {
                    state = _merge(state, pState);
                    iterations++;
                    stateVersion++;
                    lastSnapshotId = await SaveSnapshotAsync(
                        workflowId, sessionId, pNode, stateVersion, state, lastSnapshotId,
                        branchedFromSessionId: null, replayedFromSnapshotId: null, branchLabel: null, cancellationToken)
                        .ConfigureAwait(false);
                    yield return new GraphStreamEvent<TState>(pNode, state, IsFinal: false);
                }
            }

            // Dynamic Send API: branch-specific states
            if (_sendEdges.TryGetValue(currentNode, out var sendRouter))
            {
                var packets = sendRouter(state) ?? Array.Empty<SendPacket<TState>>();
                foreach (var packet in packets)
                {
                    if (!_nodes.ContainsKey(packet.NodeName))
                        throw new InvalidOperationException(
                            $"Send edge from '{currentNode}' routed to unregistered node '{packet.NodeName}'.");
                }

                var sendTasks = packets
                    .Select(packet => ExecuteNodeAsync(packet.NodeName, packet.State, cancellationToken).AsTask())
                    .ToArray();
                var sendStates = await Task.WhenAll(sendTasks).ConfigureAwait(false);

                foreach (var (packet, branchState) in packets.Zip(sendStates))
                {
                    state = _merge(state, branchState);
                    iterations++;
                    stateVersion++;
                    lastSnapshotId = await SaveSnapshotAsync(
                        workflowId, sessionId, packet.NodeName, stateVersion, state, lastSnapshotId,
                        branchedFromSessionId: null, replayedFromSnapshotId: null, branchLabel: null, cancellationToken)
                        .ConfigureAwait(false);
                    yield return new GraphStreamEvent<TState>(packet.NodeName, state, IsFinal: false);
                }
            }

            // Rolling checkpoint
            if (_checkpointer is not null)
            {
                await _checkpointer.SaveAsync(new WorkflowSnapshot(
                    WorkflowId: workflowId,
                    StateVersion: stateVersion,
                    SessionId: sessionId,
                    StateJson: JsonSerializer.Serialize(state)), cancellationToken).ConfigureAwait(false);
            }

            // Route to next node
            if (_conditionalEdges.TryGetValue(currentNode, out var router))
                currentNode = router(state);
            else if (_edges.TryGetValue(currentNode, out var edgeTarget))
                currentNode = edgeTarget;
            else
                currentNode = End; // implicit END
        }

        // Terminal sentinel — consumers can check NodeName == End || IsFinal
        yield return new GraphStreamEvent<TState>(
            End, state, IsFinal: true, Reason: GraphRunReason.Completed);
    }

    /// <summary>
    /// Resumes graph execution from a previously saved <see cref="GraphSnapshot"/>,
    /// creating a new session (branch) that starts from the persisted state.
    /// </summary>
    /// <remarks>
    /// Requires an <see cref="IGraphHistoryStore"/> to have been supplied at construction time.
    /// </remarks>
    /// <param name="snapshotId">ID of the snapshot to resume from.</param>
    /// <param name="startNode">
    /// The node at which the new branch begins executing. Typically the node that follows
    /// the snapshot's <see cref="GraphSnapshot.NodeName"/> in the graph, but any registered
    /// node is valid.
    /// </param>
    /// <param name="newSessionId">
    /// Optional explicit session ID for the branched run. A random GUID-based ID is used
    /// when <see langword="null"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no <see cref="IGraphHistoryStore"/> was provided at construction time,
    /// or when the state JSON cannot be deserialized into <typeparamref name="TState"/>.
    /// </exception>
    /// <exception cref="KeyNotFoundException">
    /// Thrown when <paramref name="snapshotId"/> is not found in the history store.
    /// </exception>
    public async ValueTask<GraphRunResult<TState>> ResumeFromSnapshotAsync(
        string snapshotId,
        string startNode,
        string? newSessionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(startNode);

        if (_historyStore is null)
            throw new InvalidOperationException(
                "ResumeFromSnapshotAsync requires an IGraphHistoryStore. " +
                "Supply one via the AgentGraph constructor parameter 'historyStore'.");

        var snapshot = await _historyStore.GetSnapshotAsync(snapshotId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                $"Snapshot '{snapshotId}' was not found in the history store.");

        var restoredState = JsonSerializer.Deserialize<TState>(snapshot.StateJson)
            ?? throw new InvalidOperationException(
                $"Deserializing state from snapshot '{snapshotId}' returned null.");

        var branchSessionId = newSessionId ?? $"branch-{Guid.NewGuid():N}";

        return await RunAsyncCore(
            startNode,
            restoredState,
            Array.Empty<AgentMessage>(),
            branchSessionId,
            branchedFromSessionId: snapshot.SessionId,
            replayedFromSnapshotId: snapshot.SnapshotId,
            branchLabel: $"branch-from-{snapshot.SnapshotId}",
            cancellationToken).ConfigureAwait(false);
    }

    // ── Visualization ────────────────────────────────────────────────────────

    /// <summary>
    /// Exports the graph topology as a Mermaid diagram string.
    /// Renders in GitHub Markdown, VS Code preview, and any Mermaid-compatible viewer.
    /// </summary>
    /// <param name="direction">
    /// Mermaid layout direction. Default: <c>"TD"</c> (top-down).
    /// Other options: <c>"LR"</c> (left-right), <c>"BT"</c>, <c>"RL"</c>.
    /// </param>
    /// <returns>A Mermaid graph definition string.</returns>
    public string ToMermaid(string direction = "TD")
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"graph {direction}");

        // Nodes
        foreach (var name in _nodes.Keys)
        {
            if (_subgraphNodes.Contains(name))
                sb.AppendLine(CultureInfo.InvariantCulture, $"    {Sanitize(name)}[[\"{name}\"]]");
            else
                sb.AppendLine(CultureInfo.InvariantCulture, $"    {Sanitize(name)}[\"{name}\"]");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"    {Sanitize(End)}([\"END\"])");

        // Unconditional edges
        foreach (var (from, to) in _edges)
            sb.AppendLine(CultureInfo.InvariantCulture, $"    {Sanitize(from)} --> {Sanitize(to)}");

        // Conditional edges
        foreach (var (from, _) in _conditionalEdges)
            sb.AppendLine(CultureInfo.InvariantCulture, $"    {Sanitize(from)} -.->|condition| {Sanitize(from)}_router{{\"router\"}}");

        // Fan-out edges
        foreach (var (from, targets) in _fanOutEdges)
            foreach (var target in targets)
                sb.AppendLine(CultureInfo.InvariantCulture, $"    {Sanitize(from)} ==>|fan-out| {Sanitize(target)}");

        // Send edges
        foreach (var (from, _) in _sendEdges)
            sb.AppendLine(CultureInfo.InvariantCulture, $"    {Sanitize(from)} -.->|send| {Sanitize(from)}_send{{\"dynamic\"}}");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Exports the graph topology as a DOT (Graphviz) diagram string.
    /// </summary>
    /// <param name="graphName">Name of the digraph. Default: <c>"AgentGraph"</c>.</param>
    /// <returns>A DOT language definition string.</returns>
    public string ToDot(string graphName = "AgentGraph")
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"digraph {graphName} {{");
        sb.AppendLine("    rankdir=TB;");

        // Nodes
        foreach (var name in _nodes.Keys)
        {
            var shape = _subgraphNodes.Contains(name) ? "doubleoctagon" : "box";
            sb.AppendLine(CultureInfo.InvariantCulture, $"    \"{name}\" [shape={shape}];");
        }

        sb.AppendLine("    \"END\" [shape=doublecircle];");

        // Unconditional edges
        foreach (var (from, to) in _edges)
        {
            var target = to == End ? "END" : to;
            sb.AppendLine(CultureInfo.InvariantCulture, $"    \"{from}\" -> \"{target}\";");
        }

        // Conditional edges
        foreach (var (from, _) in _conditionalEdges)
            sb.AppendLine(CultureInfo.InvariantCulture, $"    \"{from}\" -> \"{from}_router\" [style=dashed, label=\"condition\"];");

        // Fan-out edges
        foreach (var (from, targets) in _fanOutEdges)
            foreach (var target in targets)
                sb.AppendLine(CultureInfo.InvariantCulture, $"    \"{from}\" -> \"{target}\" [style=bold, label=\"fan-out\"];");

        // Send edges
        foreach (var (from, _) in _sendEdges)
            sb.AppendLine(CultureInfo.InvariantCulture, $"    \"{from}\" -> \"{from}_send\" [style=dashed, label=\"send\"];");

        sb.AppendLine("}");
        return sb.ToString().TrimEnd();
    }

    private static string Sanitize(string name) =>
        name.Replace(" ", "_").Replace("-", "_").Replace(".", "_");

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async ValueTask<TState> ExecuteNodeAsync(
        string nodeName, TState state, CancellationToken ct)
    {
        var nodeDelegate = _nodes[nodeName];
        var session = _subgraphNodes.Contains(nodeName)
            ? NullAgentSession.Instance
            : OpenSessionForNode(nodeName);
        try
        {
            return await nodeDelegate(state, session, ct).ConfigureAwait(false);
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>
    /// Saves a snapshot to the <see cref="IGraphHistoryStore"/> if one was provided;
    /// returns the new snapshot ID, or the existing <paramref name="previousSnapshotId"/>
    /// when no store is configured.
    /// </summary>
    private async ValueTask<string?> SaveSnapshotAsync(
        string workflowId, string sessionId, string nodeName,
        int stateVersion, TState state, string? previousSnapshotId,
        string? branchedFromSessionId,
        string? replayedFromSnapshotId,
        string? branchLabel,
        CancellationToken ct)
    {
        if (_historyStore is null) return previousSnapshotId;

        var snapshotId = $"snap-{Guid.NewGuid():N}";

        await _historyStore.SaveSnapshotAsync(new GraphSnapshot(
            SnapshotId: snapshotId,
            SessionId: sessionId,
            WorkflowId: workflowId,
            NodeName: nodeName,
            StateVersion: stateVersion,
            StateJson: JsonSerializer.Serialize(state),
            CreatedAt: DateTimeOffset.UtcNow,
            ParentSnapshotId: previousSnapshotId,
            BranchedFromSessionId: branchedFromSessionId)
        {
            ReplayedFromSnapshotId = replayedFromSnapshotId,
            BranchLabel = branchLabel
        }, ct).ConfigureAwait(false);

        return snapshotId;
    }

    private IAgentSession OpenSessionForNode(string nodeName)
    {
        var definition = _agentDefinitions.FirstOrDefault(d => d.Name == nodeName)
            ?? throw new InvalidOperationException(
                $"No AgentDefinition registered for node '{nodeName}'. " +
                $"Pass all agent definitions to the AgentGraph constructor.");

        return _sessionFactory.CreateSession($"graph-{nodeName}-{Guid.NewGuid():N}", definition);
    }

    private void ValidateGraph(string startNode)
    {
        if (!_nodes.ContainsKey(startNode))
            throw new InvalidOperationException(
                $"Start node '{startNode}' is not registered. Call AddNode(\"{startNode}\", ...) first.");

        // Verify all unconditional edge targets are either registered nodes or END
        foreach (var (from, to) in _edges)
        {
            if (to != End && !_nodes.ContainsKey(to))
                throw new InvalidOperationException(
                    $"Edge from '{from}' points to unregistered node '{to}'.");
        }

        // Verify all fan-out targets are registered nodes
        foreach (var (fanOutNode, parallelNodes) in _fanOutEdges)
        {
            foreach (var pNode in parallelNodes)
            {
                if (!_nodes.ContainsKey(pNode))
                    throw new InvalidOperationException(
                        $"Fan-out from '{fanOutNode}' references unregistered node '{pNode}'. " +
                        $"Call AddNode(\"{pNode}\", ...) before RunAsync.");
            }
        }

    }
}
