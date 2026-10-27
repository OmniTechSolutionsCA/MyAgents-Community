namespace MyAgents.Memory;

/// <summary>
/// Long-term episodic memory record capturing a distilled interaction, session summary, or cross-session milestone.
/// </summary>
public sealed record EpisodicMemoryEntry(
    string EpisodeId,
    string SessionId,
    string Summary,
    IReadOnlyList<string> KeyFacts,
    IReadOnlyList<string> Entities,
    DateTimeOffset Timestamp,
    double Salience = 1.0,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Query criteria for recalling episodic memories across sessions.
/// </summary>
public sealed record EpisodicRecallQuery(
    string Query,
    string? SessionIdFilter = null,
    int MaxEpisodes = 5,
    double MinSalience = 0.0,
    TimeSpan? MaxAge = null);

/// <summary>
/// Store contract for persisting, querying, and consolidating episodic long-term memory.
/// </summary>
public interface IEpisodicMemoryStore
{
    /// <summary>
    /// Persists an episodic memory entry.
    /// </summary>
    ValueTask SaveEpisodeAsync(EpisodicMemoryEntry episode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recalls relevant episodic memories matching the query using hybrid semantic and salience scoring.
    /// </summary>
    ValueTask<IReadOnlyList<EpisodicMemoryEntry>> RecallEpisodesAsync(
        EpisodicRecallQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all episodes recorded for a specific session ID, ordered by timestamp ascending.
    /// </summary>
    ValueTask<IReadOnlyList<EpisodicMemoryEntry>> GetSessionEpisodesAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Consolidates episodes older than a given age into a consolidated episodic milestone.
    /// </summary>
    ValueTask ConsolidateEpisodesAsync(
        string sessionId,
        TimeSpan olderThan,
        CancellationToken cancellationToken = default);
}