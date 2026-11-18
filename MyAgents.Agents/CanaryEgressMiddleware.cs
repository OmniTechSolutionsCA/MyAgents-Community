using System.Text;
using MyAgents.Core;
using MyAgents.Core.Security;

namespace MyAgents.Agents;

/// <summary>
/// Buffers a turn, scans its output for session canaries and configured heuristics, and replaces the
/// output with a uniform refusal when the configured handler says to abort.
/// </summary>
/// <remarks>
/// <para>
/// Streaming is buffered, not incremental, following the same reasoning as
/// <see cref="GuardrailMiddleware"/>: text already handed to a caller cannot be retracted, and a
/// canary may appear in the last few tokens of a reply. A violating turn therefore emits only the
/// refusal — the clean prefix that preceded the violation is never released.
/// </para>
/// <para>
/// The refusal is a single fixed string and never names the detector that fired. A caller that
/// echoes the failure reason back to the model would otherwise hand an attacker the exact feedback
/// needed to iterate until a payload gets through.
/// </para>
/// </remarks>
internal sealed class CanaryEgressMiddleware : IAgentMiddleware
{
    /// <summary>
    /// The one message a blocked turn produces. Matches the guardrail fallback so a caller cannot
    /// distinguish "content policy" from "egress defense" by the text it receives.
    /// </summary>
    internal const string RefusalText = "I'm sorry, but I cannot provide a response to that request.";

    private readonly ICanaryVault _vault;
    private readonly IEgressScanner? _scanner;
    private readonly ICanaryViolationHandler _handler;
    private readonly CanaryDefenseOptions _options;
    private readonly CanaryTurnWindow? _window;

    public CanaryEgressMiddleware(
        ICanaryVault vault,
        IEgressScanner? scanner,
        ICanaryViolationHandler handler,
        CanaryDefenseOptions options,
        CanaryTurnWindow? window = null)
    {
        _vault = vault;
        _scanner = scanner;
        _handler = handler;
        _options = options;
        _window = window;
    }

    public async ValueTask<AgentTurnResult> InvokeAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>> next,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // Quarantine is sticky, so there is no point spending a model call on this turn.
        if (_vault.IsQuarantined)
            return Refuse();

        var result = await next(context, cancellationToken).ConfigureAwait(false);
        return await ApplyAsync(context, result, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, IAsyncEnumerable<AgentTurnResult>> next,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (_vault.IsQuarantined)
        {
            yield return Refuse();
            yield break;
        }

        var chunks = new List<AgentTurnResult>();
        await foreach (var chunk in next(context, cancellationToken).ConfigureAwait(false))
            chunks.Add(chunk);

        var accumulated = AgentOutputAccumulator.Fold(chunks);
        if (accumulated is null)
            yield break;

        yield return await ApplyAsync(context, accumulated, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AgentTurnResult> ApplyAsync(
        AgentTurnContext context,
        AgentTurnResult result,
        CancellationToken cancellationToken)
    {
        var payload = BuildPayload(result.Output);

        // Scan the recent-turn window together with this turn. Scanning the turn alone is what lets
        // an attacker emit one canary fragment per turn and never present a whole token.
        var scanned = _window is null ? payload : string.Concat(_window.Snapshot(), payload);

        var scan = await EgressGuard
            .ScanAsync(
                _vault.Canaries,
                _scanner,
                EgressChannel.TurnOutput,
                scanned,
                _options.MaxScanBytes,
                _options.FailOpenOnScannerError,
                cancellationToken)
            .ConfigureAwait(false);

        if (scan.Severity == EgressSeverity.Informational)
        {
            // Only clean turns enter the window: retaining a violating turn would keep feeding the
            // detector text it has already rejected.
            _window?.Append(payload);
            return result;
        }

        var disposition = await _handler
            .HandleAsync(new CanaryViolationContext(EgressChannel.TurnOutput, scan, context.SessionId), cancellationToken)
            .ConfigureAwait(false);

        // Replace the output outright. Appending the refusal would still deliver the leaked text.
        return disposition == CanaryDisposition.Abort ? Refuse() : result;
    }

    private static AgentTurnResult Refuse()
        => new(
        [
            new AgentMessage("assistant", RefusalText),
        ],
        new AgentTurnTelemetry(TimeSpan.Zero));

    /// <summary>
    /// Flattens the turn's messages into the text that will actually be handed to the caller,
    /// including text carried inside multi-modal parts — an adapter renders those, so a canary
    /// hidden in a content part leaves the process just as surely as one in <c>Content</c>.
    /// </summary>
    private static string BuildPayload(IReadOnlyList<AgentMessage> output)
    {
        if (output.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var message in output)
        {
            if (message.ContentParts is { Count: > 0 } parts)
            {
                foreach (var part in parts)
                {
                    if (part is TextAgentContent text)
                        builder.AppendLine(text.Text);
                }
            }

            builder.AppendLine(message.Content);
        }

        return builder.ToString();
    }
}
