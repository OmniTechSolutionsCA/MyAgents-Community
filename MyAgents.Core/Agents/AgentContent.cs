namespace MyAgents.Core;

/// <summary>
/// Base type for all content parts that can appear in a multi-modal <see cref="AgentMessage"/>.
/// Core defines the discriminated union; adapter layers map each case to their native content type.
/// </summary>
public abstract record AgentContent;

/// <summary>Plain text content part.</summary>
/// <param name="Text">The text value.</param>
public sealed record TextAgentContent(string Text) : AgentContent;

/// <summary>
/// Binary or base-64 image content part.
/// </summary>
/// <param name="Data">Raw image bytes.</param>
/// <param name="MediaType">MIME type, e.g. "image/png", "image/jpeg".</param>
public sealed record ImageAgentContent(ReadOnlyMemory<byte> Data, string MediaType) : AgentContent;

/// <summary>
/// Arbitrary binary data content part (audio, video, PDF, etc.).
/// </summary>
/// <param name="Data">Raw bytes.</param>
/// <param name="MediaType">MIME type describing the data format.</param>
public sealed record DataAgentContent(ReadOnlyMemory<byte> Data, string MediaType) : AgentContent;

/// <summary>
/// Audio content part with optional metadata.
/// </summary>
/// <param name="Data">Raw audio bytes.</param>
/// <param name="MediaType">MIME type, for example "audio/wav" or "audio/mpeg".</param>
/// <param name="Duration">Optional audio duration.</param>
/// <param name="Language">Optional BCP-47 language tag for spoken language.</param>
public sealed record AudioAgentContent(
	ReadOnlyMemory<byte> Data,
	string MediaType,
	TimeSpan? Duration = null,
	string? Language = null) : AgentContent;

/// <summary>
/// Video content part with optional metadata.
/// </summary>
/// <param name="Data">Raw video bytes.</param>
/// <param name="MediaType">MIME type, for example "video/mp4".</param>
/// <param name="Duration">Optional video duration.</param>
/// <param name="Width">Optional video width in pixels.</param>
/// <param name="Height">Optional video height in pixels.</param>
public sealed record VideoAgentContent(
	ReadOnlyMemory<byte> Data,
	string MediaType,
	TimeSpan? Duration = null,
	int? Width = null,
	int? Height = null) : AgentContent;

/// <summary>
/// Document content part for files such as PDF and Office documents.
/// </summary>
/// <param name="Data">Raw document bytes.</param>
/// <param name="MediaType">MIME type, for example "application/pdf".</param>
/// <param name="FileName">Optional original file name.</param>
public sealed record DocumentAgentContent(
	ReadOnlyMemory<byte> Data,
	string MediaType,
	string? FileName = null) : AgentContent;

/// <summary>
/// URI-based image content part.
/// </summary>
/// <param name="Uri">Image URI.</param>
/// <param name="MediaType">Optional MIME type when known.</param>
public sealed record UriImageAgentContent(Uri Uri, string? MediaType = null) : AgentContent;
