using System.Globalization;
using System.Text.Json;
using MyAgents.Core.Memory;

namespace MyAgents.Memory;

/// <summary>
/// Hybrid episodic memory store providing long-term semantic persistence, cross-session recall,
/// and automated milestone consolidation.
/// </summary>
public sealed class EpisodicMemoryStore : IEpisodicMemoryStore
{
    public const string DefaultNamespace = "episodic-memory";

    private readonly IMemoryStore _underlyingStore;
    private readonly IHybridMemoryRetriever _retriever;
    private readonly string _namespace;

    public EpisodicMemoryStore(
        IMemoryStore underlyingStore,
        IHybridMemoryRetriever retriever,
        string ns = DefaultNamespace)
    {
        _underlyingStore = underlyingStore ?? throw new ArgumentNullException(nameof(underlyingStore));
        _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));
        _namespace = string.IsNullOrWhiteSpace(ns) ? DefaultNamespace : ns;
    }

    /// <inheritdoc />
    public async ValueTask SaveEpisodeAsync(EpisodicMemoryEntry episode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episode);

        var entry = ToMemoryEntry(episode);
        await _underlyingStore.SetAsync(entry, cancellationToken).ConfigureAwait(false);
        _retriever.IndexLexicalEntry(_namespace, entry);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<EpisodicMemoryEntry>> RecallEpisodesAsync(
        EpisodicRecallQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var hybridQuery = new HybridMemoryQuery(
            QueryText: query.Query,
            MaxResults: query.MaxEpisodes * 3);

        var searchResults = await _retriever.SearchHybridAsync(_namespace, hybridQuery, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        var episodes = new List<EpisodicMemoryEntry>();
        foreach (var r in searchResults)
        {
            var episode = FromMemoryEntry(r.Entry);
            if (episode is null) continue;

            if (!string.IsNullOrWhiteSpace(query.SessionIdFilter) &&
                !string.Equals(episode.SessionId, query.SessionIdFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (episode.Salience < query.MinSalience)
            {
                continue;
            }

            if (query.MaxAge.HasValue && (now - episode.Timestamp) > query.MaxAge.Value)
            {
                continue;
            }

            episodes.Add(episode);
            if (episodes.Count >= query.MaxEpisodes)
            {
                break;
            }
        }

        return episodes;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<EpisodicMemoryEntry>> GetSessionEpisodesAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var entries = await _underlyingStore.SearchAsync(_namespace, maxResults: 1000, cancellationToken: cancellationToken).ConfigureAwait(false);
        var list = new List<EpisodicMemoryEntry>();

        foreach (var entry in entries)
        {
            var episode = FromMemoryEntry(entry);
            if (episode is not null && string.Equals(episode.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(episode);
            }
        }

        return list.OrderBy(e => e.Timestamp).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask ConsolidateEpisodesAsync(
        string sessionId,
        TimeSpan olderThan,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var episodes = await GetSessionEpisodesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var cutoff = DateTimeOffset.UtcNow - olderThan;
        var toConsolidate = episodes.Where(e => e.Timestamp < cutoff).ToList();

        if (toConsolidate.Count <= 1)
        {
            return; // Nothing to merge
        }

        var consolidatedSummary = string.Join("\n\n", toConsolidate.Select(e => $"[{e.Timestamp:yyyy-MM-dd}] {e.Summary}"));
        var distinctFacts = toConsolidate.SelectMany(e => e.KeyFacts).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var distinctEntities = toConsolidate.SelectMany(e => e.Entities).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var maxSalience = toConsolidate.Max(e => e.Salience);

        var consolidatedEpisode = new EpisodicMemoryEntry(
            EpisodeId: $"consolidated-{Guid.NewGuid():N}",
            SessionId: sessionId,
            Summary: consolidatedSummary,
            KeyFacts: distinctFacts,
            Entities: distinctEntities,
            Timestamp: toConsolidate.Max(e => e.Timestamp),
            Salience: maxSalience,
            Metadata: new Dictionary<string, string> { ["consolidated_count"] = toConsolidate.Count.ToString(CultureInfo.InvariantCulture) });

        // Save new consolidated episode
        await SaveEpisodeAsync(consolidatedEpisode, cancellationToken).ConfigureAwait(false);

        // Remove old granular episodes
        foreach (var old in toConsolidate)
        {
            await _underlyingStore.DeleteAsync(_namespace, old.EpisodeId, cancellationToken).ConfigureAwait(false);
            _retriever.RemoveLexicalEntry(_namespace, old.EpisodeId);
        }
    }

    private MemoryEntry ToMemoryEntry(EpisodicMemoryEntry episode)
    {
        var factsText = episode.KeyFacts.Count > 0 ? $"Facts: {string.Join("; ", episode.KeyFacts)}" : string.Empty;
        var entitiesText = episode.Entities.Count > 0 ? $"Entities: {string.Join(", ", episode.Entities)}" : string.Empty;
        var combinedContent = $"{episode.Summary}\n{factsText}\n{entitiesText}".Trim();

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["session_id"] = episode.SessionId,
            ["key_facts"] = JsonSerializer.Serialize(episode.KeyFacts),
            ["entities"] = JsonSerializer.Serialize(episode.Entities),
            ["salience"] = episode.Salience.ToString("R", CultureInfo.InvariantCulture),
            ["timestamp"] = episode.Timestamp.ToString("O")
        };

        if (episode.Metadata is not null)
        {
            foreach (var (k, v) in episode.Metadata)
            {
                metadata[k] = v;
            }
        }

        return new MemoryEntry(
            Key: episode.EpisodeId,
            Namespace: _namespace,
            Content: combinedContent,
            Metadata: metadata,
            CreatedAt: episode.Timestamp);
    }

    private static EpisodicMemoryEntry? FromMemoryEntry(MemoryEntry entry)
    {
        if (entry.Metadata is null || !entry.Metadata.TryGetValue("session_id", out var sidObj) || sidObj is null)
        {
            return null;
        }

        var sessionId = sidObj;
        var salience = 1.0;
        if (entry.Metadata.TryGetValue("salience", out var salStr) && !string.IsNullOrWhiteSpace(salStr))
        {
            if (double.TryParse(salStr, out var parsed)) salience = parsed;
        }

        var keyFacts = new List<string>();
        if (entry.Metadata.TryGetValue("key_facts", out var kfObj) && !string.IsNullOrWhiteSpace(kfObj))
        {
            try
            {
                var facts = JsonSerializer.Deserialize<List<string>>(kfObj);
                if (facts is not null) keyFacts = facts;
            }
            catch { }
        }

        var entities = new List<string>();
        if (entry.Metadata.TryGetValue("entities", out var entObj) && !string.IsNullOrWhiteSpace(entObj))
        {
            try
            {
                var ents = JsonSerializer.Deserialize<List<string>>(entObj);
                if (ents is not null) entities = ents;
            }
            catch { }
        }

        return new EpisodicMemoryEntry(
            EpisodeId: entry.Key,
            SessionId: sessionId,
            Summary: entry.Content,
            KeyFacts: keyFacts,
            Entities: entities,
            Timestamp: entry.CreatedAt ?? DateTimeOffset.UtcNow,
            Salience: salience);
    }
}
