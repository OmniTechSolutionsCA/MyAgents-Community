namespace MyAgents.Core;

/// <summary>
/// Middleware that intercepts agent turn execution.
/// </summary>
public interface IAgentMiddleware
{
    /// <summary>
    /// Invokes the middleware for a blocking (non-streaming) turn.
    /// </summary>
    /// <param name="context">The turn context.</param>
    /// <param name="next">The next middleware or agent in the chain.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the turn execution.</returns>
    ValueTask<AgentTurnResult> InvokeAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>> next,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes the middleware for a streaming turn.
    /// The default implementation delegates to <see cref="InvokeAsync"/> and yields the result as
    /// a single chunk, so existing middleware participates in streaming without any changes.
    /// Override to implement true streaming interception.
    /// </summary>
    /// <remarks>
    /// A middleware that relies on this default <b>narrows the stream to a single chunk</b>: the
    /// chunks are accumulated into the shape a blocking turn would have produced (see
    /// <c>AgentOutputAccumulator</c>), the blocking <see cref="InvokeAsync"/> path runs once against
    /// that result, and the outcome is yielded at the end of the turn. Callers receive the full
    /// accumulated content, but not incrementally — token-by-token delivery requires overriding
    /// this method.
    /// </remarks>
    /// <param name="context">The turn context.</param>
    /// <param name="next">The next streaming middleware or agent in the chain.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of partial results.</returns>
    IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, IAsyncEnumerable<AgentTurnResult>> next,
        CancellationToken cancellationToken = default)
        => DefaultStreamingInvoke(this, context, next, cancellationToken);

    // ── Default implementation ───────────────────────────────────────────────

    private static async IAsyncEnumerable<AgentTurnResult> DefaultStreamingInvoke(
        IAgentMiddleware middleware,
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, IAsyncEnumerable<AgentTurnResult>> next,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Bridge: run the blocking path through this middleware, then yield the result.
        // This means all existing middleware (Resilience, Fallback, Telemetry, Logging) automatically
        // participates in the streaming path without being rewritten.
        //
        // Blocking middlewares narrow the stream to a single chunk, so the accumulated chunks must
        // first be folded into the shape a blocking turn would have produced. Yielding chunks
        // unchanged would hand downstream middleware fragmented messages and cumulative-average
        // telemetry, and yielding only the final chunk would discard everything emitted before it.
        var blockingNext = new Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>>(
            async (ctx, ct) =>
            {
                var chunks = new List<AgentTurnResult>();
                await foreach (var chunk in next(ctx, ct).ConfigureAwait(false))
                    chunks.Add(chunk);

                return AgentOutputAccumulator.Fold(chunks)
                    ?? new AgentTurnResult(Array.Empty<AgentMessage>(), new AgentTurnTelemetry(TimeSpan.Zero));
            });

        var result = await middleware.InvokeAsync(context, blockingNext, cancellationToken).ConfigureAwait(false);
        yield return result;
    }
}
