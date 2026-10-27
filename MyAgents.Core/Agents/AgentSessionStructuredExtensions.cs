using System.Text.Json;

namespace MyAgents.Core;

/// <summary>
/// Extensions on <see cref="IAgentSession"/> for strongly-typed structured output invocations.
/// </summary>
public static class AgentSessionStructuredExtensions
{
    /// <summary>
    /// Invokes a turn requesting structured output conforming to the schema of <typeparamref name="T"/>,
    /// and deserializes the assistant response to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The target structured type.</typeparam>
    /// <param name="session">The active agent session.</param>
    /// <param name="messages">The turn input messages.</param>
    /// <param name="jsonSerializerOptions">Optional JSON serializer options.</param>
    /// <param name="strict">Whether strict schema matching is requested.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A strongly typed result containing the deserialized value and turn execution details.</returns>
    public static async ValueTask<AgentStructuredTurnResult<T>> InvokeAsync<T>(
        this IAgentSession session,
        IReadOnlyList<AgentMessage> messages,
        JsonSerializerOptions? jsonSerializerOptions = null,
        bool? strict = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(messages);

        var format = AgentResponseFormat.ForType<T>(
            options: jsonSerializerOptions,
            strict: strict);

        var turnResult = await session.InvokeAsync(
            messages,
            ctx => ctx.Capabilities.Set(format),
            cancellationToken).ConfigureAwait(false);

        var assistantMessage = turnResult.Output.LastOrDefault(static m =>
            string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The agent turn produced no assistant message to deserialize.");

        var content = assistantMessage.Content?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("The assistant produced an empty response when structured output was expected.");

        // Strip markdown code fences if model wrapped response in ```json ... ```
        if (content.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            content = content["```json".Length..];
            if (content.EndsWith("```", StringComparison.Ordinal))
                content = content[..^3];
            content = content.Trim();
        }
        else if (content.StartsWith("```", StringComparison.Ordinal))
        {
            content = content[3..];
            if (content.EndsWith("```", StringComparison.Ordinal))
                content = content[..^3];
            content = content.Trim();
        }

        var opts = jsonSerializerOptions ?? JsonSerializerOptions.Default;
        var value = JsonSerializer.Deserialize<T>(content, opts)
            ?? throw new InvalidOperationException($"Failed to deserialize assistant response into type '{typeof(T).FullName}'. Content: {content}");

        return new AgentStructuredTurnResult<T>(value, turnResult);
    }
}
