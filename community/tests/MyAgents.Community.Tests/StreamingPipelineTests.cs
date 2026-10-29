using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyAgents.Agents;
using MyAgents.Core;
using MyAgents.Runtime.Chat;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

/// <summary>
/// Regression tests for the streaming (async) side of the agent pipeline.
/// </summary>
/// <remarks>
/// <para>
/// These pin two distinct defects that both stem from streaming being bolted onto a
/// blocking pipeline rather than designed as a first-class path:
/// </para>
/// <list type="number">
/// <item>
/// <b>Content loss.</b> A middleware that only implements the blocking
/// <see cref="IAgentMiddleware.InvokeAsync"/> is bridged into the streaming chain by
/// <c>IAgentMiddleware.DefaultStreamingInvoke</c>. That bridge accumulated chunks but
/// returned only the last one, so registering any such middleware silently truncated
/// the caller's stream to its final delta.
/// </item>
/// <item>
/// <b>Guardrail bypass.</b> <see cref="GuardrailMiddleware"/> inspected only the final
/// delta of a stream, and applied its verdict after every earlier delta had already been
/// yielded. A blocked phrase split across deltas was therefore neither detected nor
/// preventable.
/// </item>
/// </list>
/// </remarks>
public sealed class StreamingPipelineTests
{
    // ── 1. Streaming must not lose content when a blocking middleware is present ──

    [Test]
    public async Task Streaming_preserves_all_deltas_when_blocking_middleware_is_registered()
    {
        var client = new ScriptedChatClient();
        client.EnqueueStream([
            new ChatResponseUpdate(ChatRole.Assistant, "Hel"),
            new ChatResponseUpdate(null, "lo, "),
            new ChatResponseUpdate(null, "wor"),
            new ChatResponseUpdate(null, "ld!"),
        ]);

        // LoggingMiddleware implements only the blocking path, so it is bridged.
        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var session = await CreateSessionAsync(provider, client, [new LoggingFeature()]);

        var emitted = await CollectStreamAsync(session);

        Assert.That(emitted, Is.EqualTo("Hello, world!"),
            "every delta must survive a blocking middleware in the streaming chain");
    }

    [Test]
    public async Task Streaming_inner_agent_without_middleware_stays_incremental()
    {
        var client = new ScriptedChatClient();
        client.EnqueueStream([
            new ChatResponseUpdate(ChatRole.Assistant, "Hel"),
            new ChatResponseUpdate(null, "lo"),
        ]);

        using var provider = new ServiceCollection().BuildServiceProvider();
        var session = await CreateSessionAsync(provider, client, []);

        var chunkCount = 0;
        await foreach (var _ in session.InvokeStreamingAsync([AgentMessage.FromText("user", "hi")]))
            chunkCount++;

        Assert.That(chunkCount, Is.EqualTo(2),
            "with no middleware the stream must stay token-incremental");
    }

    [Test]
    public async Task Streaming_with_LoggingFeature_stays_token_incremental()
    {
        var client = new ScriptedChatClient();
        client.EnqueueStream([
            new ChatResponseUpdate(ChatRole.Assistant, "Hel"),
            new ChatResponseUpdate(null, "lo, "),
            new ChatResponseUpdate(null, "wor"),
            new ChatResponseUpdate(null, "ld!"),
        ]);

        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var session = await CreateSessionAsync(provider, client, [new LoggingFeature()]);

        var chunkCount = 0;
        await foreach (var _ in session.InvokeStreamingAsync([AgentMessage.FromText("user", "hi")]))
            chunkCount++;

        Assert.That(chunkCount, Is.EqualTo(4),
            "LoggingFeature should preserve streaming incrementality rather than collapsing to a single chunk");
    }

    [Test]
    public async Task Streaming_with_LoggingFeature_propagates_exception_and_records_failure()
    {
        var client = new ScriptedChatClient();
        client.EnqueueStream([
            new ChatResponseUpdate(ChatRole.Assistant, "Starting stream..."),
        ]);
        client.ThrowOnStream(new InvalidOperationException("Upstream LLM network error"));

        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var session = await CreateSessionAsync(provider, client, [new LoggingFeature()]);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in session.InvokeStreamingAsync([AgentMessage.FromText("user", "hi")]))
            {
            }
        });

        Assert.That(ex!.Message, Is.EqualTo("Upstream LLM network error"));
    }

    [Test]
    public async Task Streaming_with_LoggingFeature_handles_cancellation_cleanly()
    {
        var client = new ScriptedChatClient();
        client.EnqueueStream([
            new ChatResponseUpdate(ChatRole.Assistant, "Chunk 1"),
            new ChatResponseUpdate(null, "Chunk 2"),
            new ChatResponseUpdate(null, "Chunk 3"),
        ]);

        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var session = await CreateSessionAsync(provider, client, [new LoggingFeature()]);

        using var cts = new CancellationTokenSource();
        var received = 0;
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in session.InvokeStreamingAsync([AgentMessage.FromText("user", "hi")], cancellationToken: cts.Token))
            {
                received++;
                cts.Cancel();
            }
        });

        Assert.That(received, Is.EqualTo(1));
    }

    // ── 2. Guardrails must not be bypassable on the streaming path ──

    [Test]
    public async Task Streaming_guardrail_blocks_phrase_split_across_deltas()
    {
        var client = new ScriptedChatClient();
        // The blocked phrase only exists after concatenation. Inspecting a single
        // delta can never see it.
        client.EnqueueStream([
            new ChatResponseUpdate(ChatRole.Assistant, "the secre"),
            new ChatResponseUpdate(null, "t is 1234"),
        ]);

        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var session = await CreateSessionAsync(
            provider,
            client,
            [new GuardrailsFeature(new BlocklistGuardrailPolicy(["secret"]))]);

        var emitted = await CollectStreamAsync(session);

        Assert.That(emitted, Does.Not.Contain("secret"),
            "a guardrail violation split across deltas must be caught");
    }

    [Test]
    public async Task Streaming_guardrail_blocks_phrase_contained_in_a_single_delta()
    {
        var client = new ScriptedChatClient();
        client.EnqueueStream([
            new ChatResponseUpdate(ChatRole.Assistant, "safe preamble. "),
            new ChatResponseUpdate(null, "the secret is 1234"),
        ]);

        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var session = await CreateSessionAsync(
            provider,
            client,
            [new GuardrailsFeature(new BlocklistGuardrailPolicy(["secret"]))]);

        var emitted = await CollectStreamAsync(session);

        Assert.Multiple(() =>
        {
            Assert.That(emitted, Does.Not.Contain("secret"),
                "a violating delta must never reach the caller");
            Assert.That(emitted, Does.Not.Contain("safe preamble"),
                "text yielded before the violation cannot be retracted, so a violation " +
                "anywhere in the turn must suppress the whole stream rather than append a replacement");
        });
    }

    [Test]
    public async Task Streaming_guardrail_passes_clean_output_through_unchanged()
    {
        var client = new ScriptedChatClient();
        client.EnqueueStream([
            new ChatResponseUpdate(ChatRole.Assistant, "all "),
            new ChatResponseUpdate(null, "clear"),
        ]);

        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var session = await CreateSessionAsync(
            provider,
            client,
            [new GuardrailsFeature(new BlocklistGuardrailPolicy(["secret"]))]);

        var emitted = await CollectStreamAsync(session);

        Assert.That(emitted, Is.EqualTo("all clear"),
            "a clean turn must not be mutated by the buffering guardrail");
    }

    [Test]
    public async Task Streaming_guardrail_uses_policy_replacement_message_on_violation()
    {
        var client = new ScriptedChatClient();
        client.EnqueueStream([
            new ChatResponseUpdate(ChatRole.Assistant, "the secret is 1234"),
        ]);

        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var session = await CreateSessionAsync(
            provider,
            client,
            [new GuardrailsFeature(new ReplacementGuardrailPolicy("blocked by policy"))]);

        var emitted = await CollectStreamAsync(session);

        Assert.That(emitted, Is.EqualTo("blocked by policy"),
            "the caller must receive the policy's replacement, not the leaked text");
    }

    [Test]
    public void Fold_merges_consecutive_same_role_fragments_into_one_message()
    {
        var chunks = new List<AgentTurnResult>
        {
            Chunk("assistant", "Hel", TimeSpan.FromMilliseconds(10), toolCalls: 1),
            Chunk("assistant", "lo, ", TimeSpan.FromMilliseconds(20), toolCalls: 1),
            Chunk("assistant", "world!", TimeSpan.FromMilliseconds(30), toolCalls: 1),
        };

        var folded = AgentOutputAccumulator.Fold(chunks);

        Assert.That(folded, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(folded!.Output, Has.Count.EqualTo(1),
                "fragments of one turn must not become separate messages");
            Assert.That(folded.Output[0].Content, Is.EqualTo("Hello, world!"));
            Assert.That(folded.Telemetry.TurnDuration, Is.EqualTo(TimeSpan.FromMilliseconds(30)),
                "duration is cumulative, so the final chunk wins");
            Assert.That(folded.Telemetry.ToolCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public void Fold_keeps_distinct_roles_apart()
    {
        var chunks = new List<AgentTurnResult>
        {
            Chunk("assistant", "answer", TimeSpan.Zero, toolCalls: 0),
            Chunk("tool", "result", TimeSpan.Zero, toolCalls: 0),
        };

        var folded = AgentOutputAccumulator.Fold(chunks);

        Assert.That(folded!.Output, Has.Count.EqualTo(2),
            "a role change starts a new message");
    }

    [Test]
    public void Fold_returns_null_for_an_empty_stream()
    {
        Assert.That(AgentOutputAccumulator.Fold([]), Is.Null);
    }

    private static AgentTurnResult Chunk(string role, string text, TimeSpan duration, int toolCalls)
        => new([AgentMessage.FromText(role, text)], new AgentTurnTelemetry(duration, toolCalls));

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<IAgentSession> CreateSessionAsync(
        IServiceProvider provider,
        IChatClient client,
        IAgentFeature[] features)
    {
        var runtime = new ChatClientRuntime(client, Options.Create(new ChatClientRuntimeOptions()));
        return await new DefaultAgentSessionFactory(runtime, provider).CreateSessionAsync(
            "stream-session",
            new AgentDefinition("stream-agent", features));
    }

    private static async Task<string> CollectStreamAsync(IAgentSession session)
    {
        var parts = new List<string>();
        await foreach (var result in session.InvokeStreamingAsync([AgentMessage.FromText("user", "hi")]))
        {
            foreach (var message in result.Output)
            {
                parts.Add(message.Content);
            }
        }

        return string.Concat(parts);
    }

    /// <summary>
    /// Guardrail policy that always violates and supplies an explicit replacement message.
    /// <see cref="BlocklistGuardrailPolicy"/> exposes no replacement, so this covers the
    /// policy-supplied replacement path instead.
    /// </summary>
    private sealed class ReplacementGuardrailPolicy(string replacement) : IGuardrailPolicy
    {
        public ValueTask<GuardrailViolation?> EvaluateAsync(
            IReadOnlyList<AgentMessage> output,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<GuardrailViolation?>(
                new GuardrailViolation(nameof(ReplacementGuardrailPolicy), "always blocks", replacement));
    }

    /// <summary>
    /// Minimal <see cref="IChatClient"/> that replays pre-scripted streaming updates.
    /// </summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<List<ChatResponseUpdate>> _streams = new();
        private Exception? _throwOnStream;

        public void EnqueueStream(List<ChatResponseUpdate> updates) => _streams.Enqueue(updates);
        public void ThrowOnStream(Exception ex) => _throwOnStream = ex;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("This test double only supports streaming.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var batch = _streams.Count > 0 ? _streams.Dequeue() : [];
            foreach (var update in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return update;
            }

            if (_throwOnStream is not null)
            {
                throw _throwOnStream;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
