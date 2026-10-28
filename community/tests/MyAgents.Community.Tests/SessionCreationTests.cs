using Microsoft.Extensions.DependencyInjection;
using MyAgents.Core;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

/// <summary>
/// Contract tests for session creation, focused on cancellation propagation.
/// </summary>
/// <remarks>
/// <see cref="DefaultAgentSessionFactory.CreateSessionAsync"/> accepts a cancellation token and
/// is documented to be the async-safe path for features that perform I/O during contribution
/// (MCP tool discovery being the motivating case). The token must therefore reach
/// <see cref="IAgentFeature.ContributeAsync(AgentAssembly, CancellationToken)"/> rather than
/// being dropped.
/// </remarks>
public sealed class SessionCreationTests
{
    [Test]
    public async Task CreateSessionAsync_forwards_cancellation_token_to_features()
    {
        var feature = new CancellationRecordingFeature();
        var factory = new TrackingSessionFactory();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(),
            async () => await factory.CreateSessionAsync("s1", new AgentDefinition("a1", [feature]), cts.Token));

        Assert.Multiple(() =>
        {
            Assert.That(feature.ReceivedToken, Is.EqualTo(cts.Token),
                "the caller's token must reach ContributeAsync, not default");
            Assert.That(feature.ReceivedToken.CanBeCanceled, Is.True);
        });
    }

    [Test]
    public async Task CreateSessionAsync_with_a_live_token_creates_the_session()
    {
        var feature = new CancellationRecordingFeature();
        var factory = new TrackingSessionFactory();

        var session = await factory.CreateSessionAsync("s1", new AgentDefinition("a1", [feature]));

        Assert.Multiple(() =>
        {
            Assert.That(session.SessionId, Is.EqualTo("s1"));
            Assert.That(feature.WasInvoked, Is.True);
        });
    }

    /// <summary>
    /// Feature that records the token it was handed and cancels itself when it is already
    /// cancelled, so the test observes propagation without needing a live provider.
    /// </summary>
    private sealed class CancellationRecordingFeature : IAgentFeature
    {
        public CancellationToken ReceivedToken { get; private set; }
        public bool WasInvoked { get; private set; }

        public void Contribute(AgentAssembly assembly) => WasInvoked = true;

        public ValueTask ContributeAsync(AgentAssembly assembly, CancellationToken cancellationToken = default)
        {
            ReceivedToken = cancellationToken;
            WasInvoked = true;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Session factory wired to a runtime that never calls a model, so creation logic can be
    /// exercised in isolation.
    /// </summary>
    private sealed class TrackingSessionFactory : IAgentSessionFactory
    {
        private readonly DefaultAgentSessionFactory _inner =
            new(new NoOpRuntime(), new ServiceCollection().BuildServiceProvider());

        public IAgentSession CreateSession(string sessionId, AgentDefinition definition)
            => _inner.CreateSession(sessionId, definition);

        public ValueTask<IAgentSession> CreateSessionAsync(
            string sessionId,
            AgentDefinition definition,
            CancellationToken cancellationToken = default)
            => _inner.CreateSessionAsync(sessionId, definition, cancellationToken);
    }

    private sealed class NoOpRuntime : IAgentRuntime
    {
        public IAgent CreateAgent(AgentDefinition definition, AgentAssembly assembly)
            => new NoOpAgent(definition);
    }

    private sealed class NoOpAgent(AgentDefinition definition) : IAgent
    {
        public string Name => definition.Name;

        public ValueTask<AgentTurnResult> InvokeAsync(
            AgentTurnContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentTurnResult([], new AgentTurnTelemetry(TimeSpan.Zero)));

        public async IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
            AgentTurnContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public void Dispose()
        {
        }
    }
}
