namespace MyAgents.Core;

/// <summary>
/// Folds the chunks of a streamed turn into the single result a blocking turn would have produced.
/// </summary>
/// <remarks>
/// <para>
/// A streaming turn emits one chunk per delta, so consecutive chunks typically carry partial
/// fragments of the same message. Treating those fragments as distinct messages breaks anything
/// that reasons about a message as a unit — most importantly <c>IGuardrailPolicy</c>, which is a
/// whole-turn contract (<c>MaxLengthGuardrailPolicy</c> sums message lengths, and the blocklist
/// policy joins messages with a separator that would otherwise sever a phrase spanning two deltas).
/// </para>
/// <para>
/// Folding gives middleware an input whose shape matches the blocking path: one message per
/// contiguous run of same-role fragments, and the last chunk's cumulative duration and token usage.
/// </para>
/// <para>
/// Use this when writing a middleware that overrides
/// <see cref="IAgentMiddleware.InvokeStreamingAsync"/> but must reason about the complete turn
/// rather than individual deltas.
/// </para>
/// </remarks>
public static class AgentOutputAccumulator
{
    /// <summary>
    /// Folds <paramref name="chunks"/> into a single result, or returns <see langword="null"/>
    /// when the stream produced no chunks at all.
    /// </summary>
    public static AgentTurnResult? Fold(IReadOnlyList<AgentTurnResult> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        if (chunks.Count == 0)
            return null;

        var first = chunks[0];
        var last = chunks[^1];
        var messages = new List<AgentMessage>();

        foreach (var chunk in chunks)
            AppendRange(messages, chunk.Output);

        // Duration and token usage are cumulative, so the final chunk wins; the tool-call count
        // and model identity are established by the first chunk that carries them.
        var telemetry = new AgentTurnTelemetry(
            TurnDuration: last.Telemetry.TurnDuration,
            ToolCalls: first.Telemetry.ToolCalls,
            PromptTokens: last.Telemetry.PromptTokens,
            CompletionTokens: last.Telemetry.CompletionTokens,
            ModelId: first.Telemetry.ModelId ?? last.Telemetry.ModelId);

        return new AgentTurnResult(messages.ToArray(), telemetry);
    }

    private static void AppendRange(List<AgentMessage> target, IReadOnlyList<AgentMessage> messages)
    {
        foreach (var message in messages)
        {
            if (target.Count > 0 && IsSameTurnFragment(target[^1], message))
            {
                var previous = target[^1];
                target[^1] = previous with
                {
                    Content = previous.Content + message.Content,
                    ContentParts = MergeParts(previous.ContentParts, message.ContentParts),
                };
            }
            else
            {
                target.Add(message);
            }
        }
    }

    private static bool IsSameTurnFragment(AgentMessage previous, AgentMessage candidate)
        => string.Equals(previous.Role, candidate.Role, StringComparison.Ordinal)
           && string.Equals(previous.CorrelationId, candidate.CorrelationId, StringComparison.Ordinal)
           && string.Equals(previous.ThreadId, candidate.ThreadId, StringComparison.Ordinal)
           && string.Equals(previous.SenderId, candidate.SenderId, StringComparison.Ordinal);

    private static IReadOnlyList<AgentContent>? MergeParts(
        IReadOnlyList<AgentContent>? previous,
        IReadOnlyList<AgentContent>? candidate)
    {
        if (previous is null)
            return candidate;

        if (candidate is null)
            return previous;

        var merged = new List<AgentContent>(previous.Count + candidate.Count);
        merged.AddRange(previous);
        merged.AddRange(candidate);
        return merged;
    }
}
