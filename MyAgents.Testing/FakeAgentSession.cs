using MyAgents.Core;

namespace MyAgents.Testing;

/// <summary>
/// A test-friendly <see cref="IAgentSession"/> that wraps an <see cref="InMemoryAgent"/>
/// and exposes all invocation records for assertion.
/// </summary>
/// <remarks>
/// Use <see cref="FakeAgentSession"/> when you want to drive a single session independently
/// of the full <see cref="InMemoryAgentRuntime"/> — for example, when testing middleware
/// or pipeline logic in isolation.
///
/// <code>
/// var session = FakeAgentSession.Create("session-1")
///     .EnqueueResponse("Hello!")
///     .EnqueueResponse("Goodbye!");
///
/// var result = await session.InvokeAsync([new AgentMessage("user", "Hi")]);
/// Assert.Equal("Hello!", result.Output[0].Content);
/// Assert.Equal(1, session.InvocationCount);
/// </code>
/// </remarks>
public sealed class FakeAgentSession : IAgentSession
{
    // Minimal no-op IServiceProvider — avoids a dependency on Microsoft.Extensions.DependencyInjection
    // (only Abstractions is available via MyAgents.Core).
    private static readonly IServiceProvider EmptyServiceProvider = new EmptyProvider();

    private sealed class EmptyProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private readonly InMemoryAgent _agent;
    private readonly AgentPipeline _pipeline;

    /// <inheritdoc />
    public string SessionId { get; }

    /// <inheritdoc />
    public IReadOnlyList<IAgentCapability> Capabilities { get; } =
        Array.Empty<IAgentCapability>();

    /// <summary>All message lists received across every invocation, in call order.</summary>
    public IReadOnlyList<IReadOnlyList<AgentMessage>> ReceivedMessages =>
        _agent.ReceivedContexts.Select(c => c.Messages).ToList();

    /// <summary>Total number of times <see cref="InvokeAsync"/> was called.</summary>
    public int InvocationCount => _agent.InvocationCount;

    /// <summary>The underlying <see cref="InMemoryAgent"/> for advanced assertions.</summary>
    public InMemoryAgent Agent => _agent;

    private FakeAgentSession(string sessionId, InMemoryAgent agent, AgentPipeline pipeline)
    {
        SessionId = sessionId;
        _agent = agent;
        _pipeline = pipeline;
    }

    /// <summary>
    /// Creates a <see cref="FakeAgentSession"/> with an empty pipeline (no middleware).
    /// </summary>
    public static FakeAgentSession Create(string sessionId = "fake-session")
    {
        var agent = new InMemoryAgent(sessionId);
        var pipeline = new PipelineBuilder().Build();
        return new FakeAgentSession(sessionId, agent, pipeline);
    }

    /// <summary>
    /// Creates a <see cref="FakeAgentSession"/> with the given middleware applied
    /// (in registration order: first registered = outermost).
    /// </summary>
    public static FakeAgentSession CreateWithMiddleware(
        string sessionId,
        params IAgentMiddleware[] middlewares)
    {
        var agent = new InMemoryAgent(sessionId);
        var builder = new PipelineBuilder();
        foreach (var mw in middlewares)
            builder.Use(mw);
        return new FakeAgentSession(sessionId, agent, builder.Build());
    }

    /// <summary>Enqueues a simple assistant text response.</summary>
    public FakeAgentSession EnqueueResponse(
        string content,
        int? promptTokens = null,
        int? completionTokens = null)
    {
        _agent.EnqueueResponse(content, promptTokens, completionTokens);
        return this;
    }

    /// <summary>Enqueues a full <see cref="AgentTurnResult"/>.</summary>
    public FakeAgentSession EnqueueResult(AgentTurnResult result)
    {
        _agent.EnqueueResult(result);
        return this;
    }

    /// <summary>Sets the fallback response when the queue is empty.</summary>
    public FakeAgentSession WithFallbackResponse(string content)
    {
        _agent.WithFallbackResponse(content);
        return this;
    }

    /// <inheritdoc />
    public ValueTask<AgentTurnResult> InvokeAsync(
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var context = new AgentTurnContext(
            SessionId,
            messages,
            new AgentTurnServices(EmptyServiceProvider));
        return _pipeline.InvokeAsync(_agent, context, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var context = new AgentTurnContext(
            SessionId,
            messages,
            new AgentTurnServices(EmptyServiceProvider));
        return _pipeline.InvokeStreamingAsync(_agent, context, cancellationToken);
    }

    public void Dispose() => _agent.Dispose();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
