using MyAgents.Core;

namespace MyAgents.Agents;

// ── Built-in guardrail policies ──────────────────────────────────────────────

/// <summary>
/// Flags turns whose combined output is empty or contains nothing but whitespace.
/// This policy is always included in the default <see cref="GuardrailsFeature"/> configuration.
/// </summary>
public sealed class EmptyResponseGuardrailPolicy : IGuardrailPolicy
{
    public ValueTask<GuardrailViolation?> EvaluateAsync(
        IReadOnlyList<AgentMessage> output,
        CancellationToken cancellationToken = default)
    {
        var isEmpty = output.Count == 0 ||
                      output.All(m => string.IsNullOrWhiteSpace(m.Content));

        var violation = isEmpty
            ? new GuardrailViolation(
                PolicyName: nameof(EmptyResponseGuardrailPolicy),
                Reason: "Agent produced an empty response.",
                ReplacementMessage: "I apologize, but I was unable to generate a response. Please try again.")
            : null;

        return ValueTask.FromResult(violation);
    }
}

/// <summary>
/// Flags turns whose combined output text exceeds <see cref="MaxLength"/> characters.
/// </summary>
public sealed class MaxLengthGuardrailPolicy : IGuardrailPolicy
{
    private readonly int _maxLength;

    /// <param name="maxLength">Maximum allowed character count across all output messages.</param>
    public MaxLengthGuardrailPolicy(int maxLength)
    {
        if (maxLength < 1)
            throw new ArgumentOutOfRangeException(nameof(maxLength), "Must be at least 1.");
        _maxLength = maxLength;
    }

    public ValueTask<GuardrailViolation?> EvaluateAsync(
        IReadOnlyList<AgentMessage> output,
        CancellationToken cancellationToken = default)
    {
        var totalLength = output.Sum(m => m.Content?.Length ?? 0);

        var violation = totalLength > _maxLength
            ? new GuardrailViolation(
                PolicyName: nameof(MaxLengthGuardrailPolicy),
                Reason: $"Output length {totalLength} exceeds maximum allowed length of {_maxLength} characters.")
            : null;

        return ValueTask.FromResult(violation);
    }
}

/// <summary>
/// Flags turns whose output contains any of the configured blocked phrases
/// (case-insensitive).
/// </summary>
public sealed class BlocklistGuardrailPolicy : IGuardrailPolicy
{
    private readonly IReadOnlyList<string> _blocklist;

    /// <param name="blockedPhrases">
    /// The phrases to block. Matching is case-insensitive substring search.
    /// </param>
    public BlocklistGuardrailPolicy(IEnumerable<string> blockedPhrases)
    {
        ArgumentNullException.ThrowIfNull(blockedPhrases);
        _blocklist = blockedPhrases.ToList().AsReadOnly();
    }

    public ValueTask<GuardrailViolation?> EvaluateAsync(
        IReadOnlyList<AgentMessage> output,
        CancellationToken cancellationToken = default)
    {
        var combinedText = string.Join('\n', output.Select(m => m.Content ?? string.Empty));

        foreach (var phrase in _blocklist)
        {
            if (combinedText.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult<GuardrailViolation?>(
                    new GuardrailViolation(
                        PolicyName: nameof(BlocklistGuardrailPolicy),
                        Reason: $"Output contains blocked phrase: '{phrase}'."));
            }
        }

        return ValueTask.FromResult<GuardrailViolation?>(null);
    }
}

// ── Guardrail middleware & feature ───────────────────────────────────────────

/// <summary>
/// Middleware that applies a ordered sequence of <see cref="IGuardrailPolicy"/> checks
/// to each agent turn's output. On the first violation the output is replaced with
/// the policy's <see cref="GuardrailViolation.ReplacementMessage"/> (or a generic
/// fallback if none is supplied) and no further policies are evaluated.
/// </summary>
/// <remarks>
/// <para>
/// Streaming is buffered, not incremental: the whole turn is accumulated, the policies are
/// evaluated against it, and only then is the outcome yielded. This is required for
/// correctness — text already handed to a caller cannot be retracted, and
/// <see cref="IGuardrailPolicy"/> is a whole-turn contract (for example
/// <see cref="MaxLengthGuardrailPolicy"/> needs the total length), so per-chunk vetoing cannot
/// honour it. A violating turn therefore emits <i>only</i> the replacement message; the clean
/// prefix that preceded the violation is never released.
/// </para>
/// <para>
/// The cost is latency parity with the blocking path: an agent using guardrails receives no
/// token-by-token streaming. That is the deliberate trade — a content-safety check that leaks
/// because of a latency preference is the wrong default.
/// </para>
/// </remarks>
public sealed class GuardrailMiddleware : IAgentMiddleware
{
    private const string GenericFallbackMessage = "I'm sorry, but I cannot provide a response to that request.";

    private readonly IReadOnlyList<IGuardrailPolicy> _policies;

    internal GuardrailMiddleware(IReadOnlyList<IGuardrailPolicy> policies)
    {
        _policies = policies;
    }

    public async ValueTask<AgentTurnResult> InvokeAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>> next,
        CancellationToken cancellationToken = default)
    {
        var result = await next(context, cancellationToken).ConfigureAwait(false);
        return await ApplyPoliciesAsync(result, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, IAsyncEnumerable<AgentTurnResult>> next,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var chunks = new List<AgentTurnResult>();
        await foreach (var chunk in next(context, cancellationToken).ConfigureAwait(false))
            chunks.Add(chunk);

        // An empty stream carries nothing for policies to act on (EmptyResponseGuardrailPolicy
        // would flag zero chunks where the blocking path flags a single empty message), so it is
        // passed straight through rather than converted into a synthetic violation.
        var accumulated = AgentOutputAccumulator.Fold(chunks);
        if (accumulated is null)
            yield break;

        yield return await ApplyPoliciesAsync(accumulated, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AgentTurnResult> ApplyPoliciesAsync(
        AgentTurnResult result,
        CancellationToken cancellationToken)
    {
        foreach (var policy in _policies)
        {
            var violation = await policy.EvaluateAsync(result.Output, cancellationToken).ConfigureAwait(false);
            if (violation is null) continue;

            var replacement = violation.ReplacementMessage ?? GenericFallbackMessage;

            // Replace the turn's output outright. Appending to the violating output would deliver
            // both the flagged text and the substitute to the caller.
            return result with
            {
                Output = [new AgentMessage("assistant", replacement)]
            };
        }
        return result;
    }
}

/// <summary>
/// Feature that adds content-safety guardrails to the agent pipeline.
/// </summary>
/// <remarks>
/// <para>
/// By default only <see cref="EmptyResponseGuardrailPolicy"/> is active.
/// Supply additional policies via the constructor to enable richer checks.
/// </para>
/// <example>
/// <code>
/// var agent = new AgentDefinition(
///     Name: "MyAgent",
///     Features: new IAgentFeature[]
///     {
///         new GuardrailsFeature(
///             new EmptyResponseGuardrailPolicy(),
///             new MaxLengthGuardrailPolicy(maxLength: 4000),
///             new BlocklistGuardrailPolicy(["confidential", "restricted"])),
///     });
/// </code>
/// </example>
/// </remarks>
public sealed class GuardrailsFeature : IAgentFeature
{
    private readonly IReadOnlyList<IGuardrailPolicy> _policies;

    /// <summary>
    /// Creates a <see cref="GuardrailsFeature"/> with the default
    /// <see cref="EmptyResponseGuardrailPolicy"/> only.
    /// </summary>
    public GuardrailsFeature()
        : this(new EmptyResponseGuardrailPolicy()) { }

    /// <summary>
    /// Creates a <see cref="GuardrailsFeature"/> with the provided policies applied in order.
    /// </summary>
    public GuardrailsFeature(params IGuardrailPolicy[] policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        if (policies.Length == 0)
            throw new ArgumentException("At least one policy must be provided.", nameof(policies));

        _policies = policies.AsReadOnly();
    }

    public void Contribute(AgentAssembly assembly)
        => assembly.Pipeline.Use(new GuardrailMiddleware(_policies));
}
