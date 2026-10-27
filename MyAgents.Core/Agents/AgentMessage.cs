namespace MyAgents.Core;

/// <summary>
/// Represents a message exchanged between agents and the runtime.
/// </summary>
/// <param name="Role">The role of the message sender (e.g., "user", "assistant", "system", "tool").</param>
/// <param name="Content">
/// The primary text content of the message. For text-only messages this is the full payload.
/// When <see cref="ContentParts"/> is set this field holds a text summary or is empty.
/// </param>
/// <param name="CorrelationId">Optional correlation ID for tracing a message across agent boundaries.</param>
/// <param name="ThreadId">Optional thread/conversation ID grouping related turns together.</param>
/// <param name="SenderId">Optional identifier for the agent or user that originated this message.</param>
/// <param name="ContentParts">
/// Optional multi-modal content parts. When present, the adapter layer maps each
/// <see cref="AgentContent"/> part to the backend's native content type.
/// If null, the message is treated as plain text via <see cref="Content"/>.
/// </param>
public sealed record AgentMessage(
    string Role,
    string Content,
    string? CorrelationId = null,
    string? ThreadId = null,
    string? SenderId = null,
    IReadOnlyList<AgentContent>? ContentParts = null)
{
    /// <summary>
    /// Creates a plain text message. Equivalent to the primary constructor with no <see cref="ContentParts"/>.
    /// </summary>
    public static AgentMessage FromText(string role, string text,
        string? correlationId = null, string? threadId = null, string? senderId = null)
        => new(role, text, correlationId, threadId, senderId);

    /// <summary>
    /// Creates a multi-modal message from an explicit list of content parts.
    /// <paramref name="textSummary"/> is stored in <see cref="Content"/> for adapters that do not
    /// support multi-modal and need a text fallback.
    /// </summary>
    public static AgentMessage FromParts(string role, IReadOnlyList<AgentContent> parts,
        string? textSummary = null,
        string? correlationId = null, string? threadId = null, string? senderId = null)
        => new(role, textSummary ?? string.Empty, correlationId, threadId, senderId, parts);

    /// <summary>
    /// Returns true when this message carries multi-modal content parts
    /// rather than a plain text payload.
    /// </summary>
    public bool IsMultiModal => ContentParts is { Count: > 0 };
}
