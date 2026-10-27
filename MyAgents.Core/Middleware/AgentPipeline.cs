namespace MyAgents.Core;

/// <summary>
/// Immutable middleware pipeline. Executes middlewares in order, ending with the inner agent.
/// </summary>
public sealed class AgentPipeline
{
    private readonly IReadOnlyList<IAgentMiddleware> _middlewares;

    internal AgentPipeline(IReadOnlyList<IAgentMiddleware> middlewares)
    {
        _middlewares = middlewares ?? throw new ArgumentNullException(nameof(middlewares));
    }

    /// <summary>
    /// Builds the execution chain for a specific inner agent.
    /// Useful for caching the composed invocation pipeline across multiple turns.
    /// </summary>
    /// <param name="agent">The inner agent to execute at the end of the chain.</param>
    /// <returns>A compiled pipeline invocation function.</returns>
    public Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>> BuildChain(IAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (_middlewares.Count == 0)
            return agent.InvokeAsync;

        Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>> current = agent.InvokeAsync;

        for (int i = _middlewares.Count - 1; i >= 0; i--)
        {
            var middleware = _middlewares[i];
            var next = current;
            current = (ctx, ct) => middleware.InvokeAsync(ctx, next, ct);
        }

        return current;
    }

    /// <summary>
    /// Builds the streaming execution chain for a specific inner agent.
    /// Useful for caching the composed streaming pipeline across multiple turns.
    /// </summary>
    /// <param name="agent">The inner agent to execute at the end of the chain.</param>
    /// <returns>A compiled pipeline streaming function.</returns>
    public Func<AgentTurnContext, CancellationToken, IAsyncEnumerable<AgentTurnResult>> BuildStreamingChain(IAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (_middlewares.Count == 0)
            return agent.InvokeStreamingAsync;

        Func<AgentTurnContext, CancellationToken, IAsyncEnumerable<AgentTurnResult>> current = agent.InvokeStreamingAsync;

        for (int i = _middlewares.Count - 1; i >= 0; i--)
        {
            var middleware = _middlewares[i];
            var next = current;
            current = (ctx, ct) => middleware.InvokeStreamingAsync(ctx, next, ct);
        }

        return current;
    }

    /// <summary>
    /// Invokes the pipeline with the given agent as the inner handler.
    /// Middleware executes in registration order; the agent is the innermost handler.
    /// </summary>
    public ValueTask<AgentTurnResult> InvokeAsync(
        IAgent agent,
        AgentTurnContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (_middlewares.Count == 0)
            return agent.InvokeAsync(context, cancellationToken);

        return BuildChain(agent)(context, cancellationToken);
    }

    /// <summary>
    /// Streams responses through the full middleware pipeline.
    /// Each middleware's <see cref="IAgentMiddleware.InvokeStreamingAsync"/> is called in order.
    /// Middlewares that don't override <c>InvokeStreamingAsync</c> automatically bridge
    /// through their blocking path and yield a single result chunk.
    /// </summary>
    public IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
        IAgent agent,
        AgentTurnContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (_middlewares.Count == 0)
            return agent.InvokeStreamingAsync(context, cancellationToken);

        return BuildStreamingChain(agent)(context, cancellationToken);
    }
}
