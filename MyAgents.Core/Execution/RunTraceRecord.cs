namespace MyAgents.Core;

/// <summary>
/// A normalized run-trace event that can be stored locally, exported to CI artifacts,
/// or projected into a local studio view.
/// </summary>
public sealed record RunTraceRecord(
    string RunId,
    string SessionId,
    RunEventKind EventKind,
    DateTimeOffset RecordedAt,
    string? WorkflowId = null,
    string? NodeName = null,
    string? ToolName = null,
    string? CorrelationId = null,
    string? PayloadJson = null,
    string? RelatedId = null);
