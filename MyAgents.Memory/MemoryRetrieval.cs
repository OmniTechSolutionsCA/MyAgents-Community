using MyAgents.Core.Memory;

namespace MyAgents.Memory;

/// <summary>
/// Structured telemetry emitted for a memory retrieval request.
/// </summary>
public sealed record MemoryRetrievalTelemetry(
    string Namespace,
    string? QueryText,
    int CandidateCount,
    int ResultCount,
    string? TopResultKey,
    double TopResultScore,
    double AverageScore,
    DateTimeOffset RecordedAt,
    IReadOnlyList<string> ResultKeys);

/// <summary>
/// Sink for memory retrieval telemetry records.
/// </summary>
public interface IMemoryTelemetrySink
{
    ValueTask RecordAsync(MemoryRetrievalTelemetry telemetry, CancellationToken cancellationToken = default);
}

/// <summary>
/// Weighted query used by the memory retriever.
/// </summary>
public sealed record MemoryQuery(
    string? Text = null,
    int MaxResults = 5,
    double SemanticWeight = 0.5,
    double RecencyWeight = 0.2,
    double ImportanceWeight = 0.2,
    double KeywordWeight = 0.1,
    double MinScore = 0.0,
    TimeSpan? RecencyHalfLife = null,
    IReadOnlyDictionary<string, string>? MetadataFilters = null);

/// <summary>
/// Ranked memory search result with per-signal scores.
/// </summary>
public sealed record MemorySearchResult(
    MemoryEntry Entry,
    double Score,
    double SemanticScore,
    double KeywordScore,
    double RecencyScore,
    double ImportanceScore);

/// <summary>
/// High-level retrieval service that ranks memories using multiple signals.
/// </summary>
public interface IMemoryRetriever
{
    ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
        string ns,
        MemoryQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Summary generator used by retention/compaction workflows.
/// </summary>
public interface IMemorySummaryGenerator
{
    ValueTask<string> GenerateAsync(
        string ns,
        IReadOnlyList<MemoryEntry> entries,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Policy used by memory retention and summarization.
/// </summary>
public sealed record MemoryRetentionPolicy(
    int MaxEntries = 1000,
    TimeSpan? MaxAge = null,
    bool SummarizeTrimmedEntries = false,
    string SummaryKeyPrefix = "summary",
    int SummarySourceCount = 10);

/// <summary>
/// Outcome of a memory retention/compaction pass.
/// </summary>
public sealed record MemoryRetentionResult(
    int DeletedCount,
    int SummarizedCount,
    MemoryEntry? SummaryEntry);
