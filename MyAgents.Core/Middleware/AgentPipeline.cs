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
    /// Invokes the pipeline with the given agent as the inner handler.
    /// Middleware executes in registration order; the agent is the innermost handler.
    /// </summary>
    public ValueTask<AgentTurnResult> InvokeAsync(
        IAgent agent,
        AgentTurnContext context,
        CancellationToken cancellationToken = default)
    {
        // Build the chain from inside-out
        Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>> current =
            (ctx, ct) => agent.InvokeAsync(ctx, ct);

        // Wrap with each middleware, outermost first in registration order
        for (int i = _middlewares.Count - 1; i >= 0; i--)
        {
            var middleware = _middlewares[i];
            var next = current;
            current = (ctx, ct) => middleware.InvokeAsync(ctx, next, ct);
        }

        return current(context, cancellationToken);
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
        // Build the streaming chain from inside-out
        Func<AgentTurnContext, CancellationToken, IAsyncEnumerable<AgentTurnResult>> current =
            (ctx, ct) => agent.InvokeStreamingAsync(ctx, ct);

        // Wrap with each middleware in reverse registration order (outermost first)
        for (int i = _middlewares.Count - 1; i >= 0; i--)
        {
            var middleware = _middlewares[i];
            var next = current;
            current = (ctx, ct) => middleware.InvokeStreamingAsync(ctx, next, ct);
        }

        return current(context, cancellationToken);
    }
}
