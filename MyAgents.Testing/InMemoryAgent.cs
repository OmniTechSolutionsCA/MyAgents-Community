using MyAgents.Core;

namespace MyAgents.Testing;

/// <summary>
/// A deterministic, in-memory <see cref="IAgent"/> implementation for unit testing.
/// Returns pre-programmed responses in order; after the queue is exhausted, returns a
/// configurable fallback response (default: empty assistant message).
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// var agent = new InMemoryAgent("TestAgent")
///     .EnqueueResponse("Paris")
///     .EnqueueResponse("France");
///
/// var result = await agent.InvokeAsync(context, ct);
/// Assert.Equal("Paris", result.Output[0].Content);
/// </code>
/// </remarks>
public sealed class InMemoryAgent : IAgent
{
    private readonly Queue<AgentTurnResult> _responseQueue = new();
    private AgentTurnResult? _fallback;

    /// <summary>Name of this agent (informational).</summary>
    public string Name { get; }

    /// <summary>All contexts received by <see cref="InvokeAsync"/>.</summary>
    public IReadOnlyList<AgentTurnContext> ReceivedContexts => _receivedContexts;
    private readonly List<AgentTurnContext> _receivedContexts = new();

    /// <summary>Total number of times <see cref="InvokeAsync"/> was called.</summary>
    public int InvocationCount => _receivedContexts.Count;

    public InMemoryAgent(string name = "InMemoryAgent")
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <summary>
    /// Enqueues a full <see cref="AgentTurnResult"/> to be returned on the next invocation.
    /// </summary>
    public InMemoryAgent EnqueueResult(AgentTurnResult result)
    {
        _responseQueue.Enqueue(result);
        return this;
    }

    /// <summary>
    /// Enqueues a simple assistant text response to be returned on the next invocation.
    /// </summary>
    public InMemoryAgent EnqueueResponse(
        string content,
        int? promptTokens = null,
        int? completionTokens = null)
    {
        var telemetry = new AgentTurnTelemetry(
            TimeSpan.Zero,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens);
        var result = new AgentTurnResult(
            [new AgentMessage("assistant", content)],
            telemetry);
        return EnqueueResult(result);
    }

    /// <summary>
    /// Sets the fallback result returned when the response queue is empty.
    /// Defaults to an empty assistant message.
    /// </summary>
    public InMemoryAgent WithFallback(AgentTurnResult fallback)
    {
        _fallback = fallback;
        return this;
    }

    /// <summary>
    /// Sets the fallback as a simple assistant text response.
    /// </summary>
    public InMemoryAgent WithFallbackResponse(string content)
    {
        _fallback = new AgentTurnResult(
            [new AgentMessage("assistant", content)],
            new AgentTurnTelemetry(TimeSpan.Zero));
        return this;
    }

    /// <inheritdoc />
    public ValueTask<AgentTurnResult> InvokeAsync(
        AgentTurnContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _receivedContexts.Add(context);

        var result = _responseQueue.Count > 0
            ? _responseQueue.Dequeue()
            : _fallback ?? new AgentTurnResult(
                [new AgentMessage("assistant", string.Empty)],
                new AgentTurnTelemetry(TimeSpan.Zero));

        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
        AgentTurnContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        // Delegate to non-streaming — stream as a single chunk
        var result = await InvokeAsync(context, cancellationToken).ConfigureAwait(false);
        yield return result;
    }

    /// <summary>
    /// Resets all recorded invocations and restores the response queue.
    /// Useful between test cases when reusing the same instance.
    /// </summary>
    public void Reset()
    {
        _receivedContexts.Clear();
        _responseQueue.Clear();
        _fallback = null;
    }

    public void Dispose() { /* nothing to release */ }
}
