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

        var chunks = new List<string>();
        await foreach (var result in session.InvokeStreamingAsync([AgentMessage.FromText("user", "hi")]))
        {
            chunks.Add(result.Output[0].Content);
        }

        Assert.Multiple(() =>
        {
            Assert.That(string.Concat(chunks), Is.EqualTo("Hello, world!"),
                "every delta must survive a blocking middleware in the streaming chain");
            Assert.That(chunks, Has.Count.EqualTo(4),
                "the stream must stay incremental, not collapse into one chunk");
        });
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

        using var provider = new ServiceCollection().BuildServiceProvider();
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

        using var provider = new ServiceCollection().BuildServiceProvider();
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

        using var provider = new ServiceCollection().BuildServiceProvider();
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

        using var provider = new ServiceCollection().BuildServiceProvider();
        var session = await CreateSessionAsync(
            provider,
            client,
            [new GuardrailsFeature(new ReplacementGuardrailPolicy("blocked by policy"))]);

        var emitted = await CollectStreamAsync(session);

        Assert.That(emitted, Is.EqualTo("blocked by policy"),
            "the caller must receive the policy's replacement, not the leaked text");
    }

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

        public void EnqueueStream(List<ChatResponseUpdate> updates) => _streams.Enqueue(updates);

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
                await Task.Yield();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
