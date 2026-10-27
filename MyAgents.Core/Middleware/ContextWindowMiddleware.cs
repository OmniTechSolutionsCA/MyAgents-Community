using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MyAgents.Core;

/// <summary>
/// Decides how an <see cref="AgentMessage"/> sequence should be trimmed when the
/// message history exceeds the configured window.
/// </summary>
public interface IContextWindowTrimmer
{
    /// <summary>
    /// Returns a reduced sequence of messages that fits within the context window.
    /// </summary>
    /// <param name="messages">The full, ordered input message history.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A (possibly identical) list of messages that should be passed to the inner
    /// pipeline. Must preserve order and must not exceed the configured window.
    /// </returns>
    ValueTask<IReadOnlyList<AgentMessage>> TrimAsync(
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Trims the message history by removing the oldest <em>non-system</em> messages
/// until the list contains at most <see cref="MaxMessages"/> entries.
/// System messages (role == "system") are always retained at their original position.
/// </summary>
/// <remarks>
/// This is the default trimmer. It is suitable when the agent's context window is
/// expressed as a maximum message count rather than a token budget.
/// For token-budget trimming, implement <see cref="IContextWindowTrimmer"/> with
/// your own token-counting logic.
/// </remarks>
public sealed class SlidingWindowTrimmer : IContextWindowTrimmer
{
    private readonly int _maxMessages;

    /// <param name="maxMessages">
    /// Maximum number of non-system messages to keep. Must be at least 1.
    /// System messages are not counted toward this limit.
    /// </param>
    public SlidingWindowTrimmer(int maxMessages)
    {
        if (maxMessages < 1)
            throw new ArgumentOutOfRangeException(nameof(maxMessages), "Must be at least 1.");
        _maxMessages = maxMessages;
    }

    public ValueTask<IReadOnlyList<AgentMessage>> TrimAsync(
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var systemMessages = messages
            .Where(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var nonSystemMessages = messages
            .Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (nonSystemMessages.Count <= _maxMessages)
            return ValueTask.FromResult<IReadOnlyList<AgentMessage>>(messages);

        // Keep most-recent non-system messages.
        var kept = nonSystemMessages.Skip(nonSystemMessages.Count - _maxMessages).ToList();

        // Reintegrate system messages at the front.
        var result = new List<AgentMessage>(systemMessages.Count + kept.Count);
        result.AddRange(systemMessages);
        result.AddRange(kept);

        return ValueTask.FromResult<IReadOnlyList<AgentMessage>>(result.AsReadOnly());
    }
}

/// <summary>
/// Middleware that trims the incoming message history before forwarding the turn to
/// the inner pipeline. This prevents context-window overflow on long-running
/// conversations without modifying call-site code.
/// </summary>
/// <remarks>
/// <para>
/// Compose this middleware early in the pipeline (before telemetry and resilience
/// middleware) so that every downstream layer sees the already-trimmed history.
/// </para>
/// <para>
/// The trimmer is invoked on every turn. For conversations well within the window
/// the trimmer returns immediately with the original list, incurring no allocations.
/// </para>
/// <example>
/// <code>
/// var agent = new AgentDefinition(
///     Name: "MyAgent",
///     Features: new IAgentFeature[]
///     {
///         new ContextWindowFeature(maxMessages: 20),
///         new TelemetryFeature(),
///     });
/// </code>
/// </example>
/// </remarks>
public sealed class ContextWindowMiddleware : IAgentMiddleware
{
    private readonly IContextWindowTrimmer _trimmer;
    private readonly ILogger _logger;

    /// <param name="trimmer">
    /// The trimming strategy to apply. Defaults to <see cref="SlidingWindowTrimmer"/>
    /// with a 20-message window.
    /// </param>
    /// <param name="logger">Optional logger. Defaults to <see cref="NullLogger"/>.</param>
    public ContextWindowMiddleware(IContextWindowTrimmer? trimmer = null, ILogger<ContextWindowMiddleware>? logger = null)
    {
        _trimmer = trimmer ?? new SlidingWindowTrimmer(maxMessages: 20);
        _logger = logger ?? NullLogger<ContextWindowMiddleware>.Instance;
    }

    public async ValueTask<AgentTurnResult> InvokeAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>> next,
        CancellationToken cancellationToken = default)
    {
        var trimmed = await _trimmer.TrimAsync(context.Messages, cancellationToken).ConfigureAwait(false);

        if (!ReferenceEquals(trimmed, context.Messages))
        {
            _logger.LogDebug(
                "Context window trimmed from {Original} to {Trimmed} messages. SessionId={SessionId}",
                context.Messages.Count, trimmed.Count, context.SessionId);
        }

        var effectiveContext = ReferenceEquals(trimmed, context.Messages)
            ? context
            : context with { Messages = trimmed };

        return await next(effectiveContext, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, IAsyncEnumerable<AgentTurnResult>> next,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var trimmed = await _trimmer.TrimAsync(context.Messages, cancellationToken).ConfigureAwait(false);

        if (!ReferenceEquals(trimmed, context.Messages))
        {
            _logger.LogDebug(
                "Context window trimmed from {Original} to {Trimmed} messages (streaming). SessionId={SessionId}",
                context.Messages.Count, trimmed.Count, context.SessionId);
        }

        var effectiveContext = ReferenceEquals(trimmed, context.Messages)
            ? context
            : context with { Messages = trimmed };

        await foreach (var chunk in next(effectiveContext, cancellationToken).ConfigureAwait(false))
            yield return chunk;
    }
}
    // ----------------------------------------------------------
    // Token-budget trimmer implementation
    // ----------------------------------------------------------

    /// <summary>
    /// Counts tokens in a text string. Implementations may target a specific
    /// tokenizer or use a heuristic approximation.
    /// </summary>
    public interface ITokenCounter
    {
        /// <summary>
        /// Returns the number of tokens contained in <paramref name="text"/>.
        /// </summary>
        /// <param name="text">The input text to measure; never <c>null</c>.</param>
        int CountTokens(string text);
    }

    /// <summary>
    /// Simple token counter that approximates 1 token per ~4 characters. This is
    /// the well‑known heuristic used by many BPE tokenizers when an exact
    /// tokenizer isn’t available.
    /// </summary>
    public sealed class CharacterTokenCounter : ITokenCounter
    {
        public int CountTokens(string text)
        {
            if (text is null)
                throw new ArgumentNullException(nameof(text));
            return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
        }
    }

    /// <summary>
    /// Trimmer that ensures the total token count for a conversation stays within
    /// a specified budget. System messages are never removed and always count
    /// toward the budget; non‑system messages are dropped oldest‑first until the
    /// remaining history fits.
    /// </summary>
    public sealed class TokenBudgetTrimmer : IContextWindowTrimmer
    {
        private readonly int _maxTokens;
        private readonly ITokenCounter _tokenCounter;

        /// <param name="maxTokens">
        /// Maximum allowed token count for the entire message history. Must be at
        /// least 1.
        /// </param>
        /// <param name="tokenCounter">
        /// Optional token counter; if <c>null</c> a <see cref="CharacterTokenCounter"/>
        /// is used.
        /// </param>
        public TokenBudgetTrimmer(int maxTokens, ITokenCounter? tokenCounter = null)
        {
            if (maxTokens < 1)
                throw new ArgumentOutOfRangeException(nameof(maxTokens), "Must be at least 1.");

            _maxTokens = maxTokens;
            _tokenCounter = tokenCounter ?? new CharacterTokenCounter();
        }

        public ValueTask<IReadOnlyList<AgentMessage>> TrimAsync(
            IReadOnlyList<AgentMessage> messages,
            CancellationToken cancellationToken = default)
        {
            if (messages is null)
                throw new ArgumentNullException(nameof(messages));

            // calculate total tokens across all messages (system + non-system)
            int total = 0;
            foreach (var m in messages)
                total += _tokenCounter.CountTokens(m.Content ?? string.Empty);

            if (total <= _maxTokens)
                return ValueTask.FromResult(messages);

            var systemMessages = messages
                .Where(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var nonSystem = messages
                .Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // drop oldest non-system messages until we’re within budget or none remain
            int currentTotal = total;
            int dropIndex = 0;
            while (currentTotal > _maxTokens && dropIndex < nonSystem.Count)
            {
                var msg = nonSystem[dropIndex];
                currentTotal -= _tokenCounter.CountTokens(msg.Content ?? string.Empty);
                dropIndex++;
            }

            // if budget still exceeded, only system messages remain (even they may
            // exceed the budget, but we cannot trim them further).
            if (currentTotal > _maxTokens)
                return ValueTask.FromResult<IReadOnlyList<AgentMessage>>(systemMessages.AsReadOnly());

            // construct the trimmed list: all system messages followed by the
            // remaining non-system messages
            var remainingNonSystem = nonSystem.Skip(dropIndex).ToList();
            var result = new List<AgentMessage>(systemMessages.Count + remainingNonSystem.Count);
            result.AddRange(systemMessages);
            result.AddRange(remainingNonSystem);

            return ValueTask.FromResult<IReadOnlyList<AgentMessage>>(result.AsReadOnly());
        }
    }