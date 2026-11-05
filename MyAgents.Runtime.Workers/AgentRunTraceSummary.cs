using MyAgents.Core;

namespace MyAgents.Runtime.Workers;

/// <summary>Payload-free trace event counts associated with one run.</summary>
public sealed record AgentRunTraceSummary(
    string RunId,
    int RecordCount,
    IReadOnlyDictionary<RunEventKind, int> EventCounts);
