namespace MyAgents.Core.Workflows;

/// <summary>
/// Orchestration helpers for common workflow patterns.
/// Workflows call these from within their RunAsync implementation.
///
/// Sequential:  steps run one after another, each receiving the prior output
/// Concurrent:  steps run in parallel, results collected
/// Handoff:     one agent processes and decides which agent to hand off to next
/// </summary>
public static class WorkflowSteps
{
    /// <summary>
    /// Runs a sequence of agent calls where each step's output messages become
    /// the input for the next step.
    /// </summary>
    /// <param name="session">The workflow session.</param>
    /// <param name="agentNames">Agent names to invoke in order.</param>
    /// <param name="initialMessages">Messages for the first agent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result from the final agent in the sequence.</returns>
    public static async ValueTask<AgentTurnResult> RunSequentialAsync(
        IWorkflowSession<object?> session,
        IReadOnlyList<string> agentNames,
        IReadOnlyList<AgentMessage> initialMessages,
        CancellationToken cancellationToken = default)
    {
        if (agentNames.Count == 0)
            throw new ArgumentException("At least one agent name is required.", nameof(agentNames));

        var currentMessages = initialMessages;
        AgentTurnResult? lastResult = null;

        foreach (var agentName in agentNames)
        {
            lastResult = await session.CallAgentAsync(agentName, currentMessages, cancellationToken)
                .ConfigureAwait(false);

            // The output of this step becomes the input for the next step
            currentMessages = lastResult.Output;
        }

        return lastResult!;
    }

    /// <summary>
    /// Generic version that works with any TState.
    /// </summary>
    public static async ValueTask<AgentTurnResult> RunSequentialAsync<TState>(
        IWorkflowSession<TState> session,
        IReadOnlyList<string> agentNames,
        IReadOnlyList<AgentMessage> initialMessages,
        CancellationToken cancellationToken = default)
    {
        if (agentNames.Count == 0)
            throw new ArgumentException("At least one agent name is required.", nameof(agentNames));

        var currentMessages = initialMessages;
        AgentTurnResult? lastResult = null;

        foreach (var agentName in agentNames)
        {
            lastResult = await session.CallAgentAsync(agentName, currentMessages, cancellationToken)
                .ConfigureAwait(false);
            currentMessages = lastResult.Output;
        }

        return lastResult!;
    }

    /// <summary>
    /// Runs multiple agent calls concurrently with the same input messages.
    /// Returns all results in order matching the input agent names.
    /// </summary>
    /// <param name="session">The workflow session.</param>
    /// <param name="agentNames">Agent names to invoke concurrently.</param>
    /// <param name="messages">Messages sent to every agent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Results in the same order as <paramref name="agentNames"/>.</returns>
    public static async ValueTask<IReadOnlyList<AgentTurnResult>> RunConcurrentAsync<TState>(
        IWorkflowSession<TState> session,
        IReadOnlyList<string> agentNames,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (agentNames.Count == 0)
            throw new ArgumentException("At least one agent name is required.", nameof(agentNames));

        var tasks = agentNames
            .Select(name => session.CallAgentAsync(name, messages, cancellationToken).AsTask())
            .ToList();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// Handoff pattern: calls an initial agent, then uses a selector function to
    /// decide which agent to hand off to next, repeating until the selector returns null.
    /// </summary>
    /// <param name="session">The workflow session.</param>
    /// <param name="firstAgentName">The first agent to invoke.</param>
    /// <param name="initialMessages">Initial input messages.</param>
    /// <param name="nextAgentSelector">
    ///     Returns the name of the next agent to invoke, or null to stop.
    ///     Receives the current result and the number of hops completed so far.
    /// </param>
    /// <param name="termination">
    ///     Optional termination condition checked after every turn.
    ///     If it returns <c>true</c>, the loop stops immediately.
    ///     Works alongside <paramref name="maxHops"/> — whichever triggers first wins.
    /// </param>
    /// <param name="maxHops">Safety cap on total handoffs (default: 10).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The final agent's result.</returns>
    public static async ValueTask<AgentTurnResult> RunHandoffAsync<TState>(
        IWorkflowSession<TState> session,
        string firstAgentName,
        IReadOnlyList<AgentMessage> initialMessages,
        Func<AgentTurnResult, int, string?> nextAgentSelector,
        ITerminationCondition? termination = null,
        int maxHops = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nextAgentSelector);

        var currentAgent = firstAgentName;
        var currentMessages = initialMessages;
        var hops = 0;

        while (true)
        {
            var result = await session.CallAgentAsync(currentAgent, currentMessages, cancellationToken)
                .ConfigureAwait(false);

            hops++;

            // Check termination condition first (content-aware stop)
            if (termination?.ShouldTerminate(result, hops) == true)
                return result;

            var nextAgent = nextAgentSelector(result, hops);
            if (nextAgent is null || hops >= maxHops)
                return result;

            currentAgent = nextAgent;
            currentMessages = result.Output;
        }
    }
    /// <summary>
    /// Streaming bridge pattern.
    /// Streams the source agent live (yielding each chunk to the <paramref name="onChunk"/> observer),
    /// compacts the buffered output into a single message, then runs the downstream agents
    /// sequentially using the compacted content as input.
    /// </summary>
    /// <remarks>
    /// Use this instead of RunSequentialAsync when you need the first agent's output to appear
    /// in real-time on a UI while the pipeline continues to completion.
    /// </remarks>
    /// <param name="session">The workflow session.</param>
    /// <param name="sourceAgentName">Agent to invoke with streaming.</param>
    /// <param name="downstreamAgentNames">Agents to invoke sequentially after streaming completes.</param>
    /// <param name="initialMessages">Messages for the source agent.</param>
    /// <param name="onChunk">Called with each streamed chunk for real-time UI delivery. May be null.</param>
    /// <param name="compact">
    ///     Optional function to compact the buffered stream into a single context message.
    ///     Defaults to joining all chunk text with newlines.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The final downstream agent result.</returns>
    public static async ValueTask<AgentTurnResult> RunStreamingBridgeAsync<TState>(
        IWorkflowSession<TState> session,
        string sourceAgentName,
        IReadOnlyList<string> downstreamAgentNames,
        IReadOnlyList<AgentMessage> initialMessages,
        Action<AgentTurnResult>? onChunk = null,
        Func<IReadOnlyList<AgentMessage>, AgentMessage>? compact = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceAgentName);
        ArgumentNullException.ThrowIfNull(downstreamAgentNames);

        // 1. Stream source agent, fire onChunk for each partial result, buffer all output
        var buffer = new List<AgentMessage>();
        await foreach (var chunk in session.CallAgentStreamingAsync(sourceAgentName, initialMessages, cancellationToken)
            .ConfigureAwait(false))
        {
            onChunk?.Invoke(chunk);
            buffer.AddRange(chunk.Output);
        }

        // 2. Compact buffered output into a single context message (avoid context bloat)
        var compactor = compact ?? DefaultCompact;
        var compacted = compactor(buffer);

        // 3. Run downstream agents sequentially: compacted output → first downstream, then chained
        if (downstreamAgentNames.Count == 0)
            return new AgentTurnResult(buffer, new AgentTurnTelemetry(TimeSpan.Zero));

        return await RunSequentialAsync(
            session,
            downstreamAgentNames,
            initialMessages: new[] { compacted },
            cancellationToken).ConfigureAwait(false);
    }

    private static AgentMessage DefaultCompact(IReadOnlyList<AgentMessage> messages)
    {
        var combined = string.Join("\n", messages
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => m.Content));
        return new AgentMessage("user", combined);
    }

    /// <summary>
    /// Pauses the workflow to request a human reply, serialising current state into a snapshot.
    /// </summary>
    /// <remarks>
    /// This method always throws <see cref="HumanInputRequiredException"/>.
    /// The workflow execution unwinds — no code after this call runs until the workflow is
    /// resumed via <see cref="WorkflowHost.ResumeWithInputAsync"/>.
    /// </remarks>
    /// <typeparam name="TState">The workflow state type.</typeparam>
    /// <param name="session">The current workflow session.</param>
    /// <param name="stepId">A stable identifier for the pause point (used for diagnostics and routing).</param>
    /// <param name="prompt">The question or instruction to present to the human.</param>
    /// <param name="cancellationToken">Cancellation token (checked before throwing).</param>
    /// <exception cref="HumanInputRequiredException">Always thrown to unwind the workflow.</exception>
    public static ValueTask RequestHumanInputAsync<TState>(
        IWorkflowSession<TState> session,
        string stepId,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = session.CreateSnapshot() with
        {
            PendingHumanInput = new HumanInputRequest(stepId, prompt)
        };
        throw new HumanInputRequiredException(snapshot);
    }

    /// <summary>
    /// Pauses the workflow until a tool approval has been decided.
    /// </summary>
    public static ValueTask RequestToolApprovalAsync<TState>(
        IWorkflowSession<TState> session,
        PendingToolApproval approval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(approval);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = session.CreateSnapshot() with
        {
            PendingToolApproval = approval
        };

        throw new HumanInputRequiredException(snapshot);
    }
}
