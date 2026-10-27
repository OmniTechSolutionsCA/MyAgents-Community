using System.Text.RegularExpressions;
using MyAgents.Core.Memory;

namespace MyAgents.Memory;

/// <summary>
/// Default weighted memory retriever over keyword and semantic stores.
/// </summary>
public sealed class DefaultMemoryRetriever : IMemoryRetriever
{
    private static readonly Regex TokenRegex = new("[A-Za-z0-9]+", RegexOptions.Compiled);

    private readonly IMemoryStore _store;
    private readonly IVectorMemoryStore? _vectorStore;
    private readonly IMemoryTelemetrySink? _telemetrySink;

    public DefaultMemoryRetriever(
        IMemoryStore store,
        IVectorMemoryStore? vectorStore = null,
        IMemoryTelemetrySink? telemetrySink = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vectorStore = vectorStore;
        _telemetrySink = telemetrySink;
    }

    public async ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(
        string ns,
        MemoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentNullException.ThrowIfNull(query);

        var candidateCount = Math.Clamp(query.MaxResults * 5, query.MaxResults, 200);
        var lexical = await _store.SearchAsync(
            ns,
            query.Text,
            candidateCount,
            cancellationToken).ConfigureAwait(false);

        Dictionary<string, float>? semanticScores = null;
        if (_vectorStore is not null && !string.IsNullOrWhiteSpace(query.Text))
        {
            semanticScores = (await _vectorStore.VectorSearchAsync(
                ns,
                query.Text,
                candidateCount,
                cancellationToken: cancellationToken).ConfigureAwait(false))
                .GroupBy(static item => item.Entry.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.Max(static item => item.Score), StringComparer.OrdinalIgnoreCase);
        }

        var semanticEntries = new List<MemoryEntry>();
        if (semanticScores is not null)
        {
            foreach (var key in semanticScores.Keys)
            {
                var entry = await _store.GetAsync(ns, key, cancellationToken).ConfigureAwait(false);
                if (entry is not null)
                    semanticEntries.Add(entry);
            }
        }

        var entries = lexical
            .Concat(semanticEntries)
            .GroupBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Where(item => MatchesMetadata(item, query.MetadataFilters))
            .ToArray();

        var halfLife = query.RecencyHalfLife ?? TimeSpan.FromDays(7);
        var ranked = entries
            .Select(entry =>
            {
                var semantic = semanticScores is not null && semanticScores.TryGetValue(entry.Key, out var score)
                    ? score
                    : 0.0;
                var keyword = ScoreKeyword(entry.Content, query.Text);
                var recency = ScoreRecency(entry.CreatedAt, halfLife);
                var importance = ScoreImportance(entry.Metadata);
                var total = query.SemanticWeight + query.KeywordWeight + query.RecencyWeight + query.ImportanceWeight;
                var combined = total <= 0
                    ? 0.0
                    : ((semantic * query.SemanticWeight) +
                       (keyword * query.KeywordWeight) +
                       (recency * query.RecencyWeight) +
                       (importance * query.ImportanceWeight)) / total;

                return new MemorySearchResult(
                    Entry: entry,
                    Score: combined,
                    SemanticScore: semantic,
                    KeywordScore: keyword,
                    RecencyScore: recency,
                    ImportanceScore: importance);
            })
            .Where(result => result.Score >= query.MinScore)
            .OrderByDescending(static result => result.Score)
            .ThenByDescending(static result => result.Entry.CreatedAt)
            .Take(query.MaxResults)
            .ToArray();

        if (_telemetrySink is not null)
        {
            var telemetry = new MemoryRetrievalTelemetry(
                Namespace: ns,
                QueryText: query.Text,
                CandidateCount: entries.Length,
                ResultCount: ranked.Length,
                TopResultKey: ranked.FirstOrDefault()?.Entry.Key,
                TopResultScore: ranked.FirstOrDefault()?.Score ?? 0.0,
                AverageScore: ranked.Length == 0 ? 0.0 : ranked.Average(static result => result.Score),
                RecordedAt: DateTimeOffset.UtcNow,
                ResultKeys: ranked.Select(static result => result.Entry.Key).ToArray());
            await _telemetrySink.RecordAsync(telemetry, cancellationToken).ConfigureAwait(false);
        }

        return ranked;
    }

    private static bool MatchesMetadata(MemoryEntry entry, IReadOnlyDictionary<string, string>? filters)
    {
        if (filters is null || filters.Count == 0)
            return true;

        if (entry.Metadata is null)
            return false;

        foreach (var pair in filters)
        {
            if (!entry.Metadata.TryGetValue(pair.Key, out var value) ||
                !string.Equals(value, pair.Value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static double ScoreKeyword(string content, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return 1.0;

        var tokens = TokenRegex.Matches(query)
            .Select(static match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tokens.Length == 0)
            return 0.0;

        var hitCount = tokens.Count(token => content.Contains(token, StringComparison.OrdinalIgnoreCase));
        return hitCount / (double)tokens.Length;
    }

    private static double ScoreRecency(DateTimeOffset? createdAt, TimeSpan halfLife)
    {
        if (createdAt is null)
            return 0.0;

        var age = DateTimeOffset.UtcNow - createdAt.Value;
        if (age <= TimeSpan.Zero)
            return 1.0;

        var halfLifeSeconds = Math.Max(halfLife.TotalSeconds, 1);
        return Math.Pow(0.5, age.TotalSeconds / halfLifeSeconds);
    }

    private static double ScoreImportance(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null)
            return 0.0;

        if (TryRead(metadata, "importance", out var importance) ||
            TryRead(metadata, "priority", out importance) ||
            TryRead(metadata, "score", out importance))
        {
            return importance > 1.0 ? Math.Clamp(importance / 100.0, 0.0, 1.0) : Math.Clamp(importance, 0.0, 1.0);
        }

        return 0.0;
    }

    private static bool TryRead(IReadOnlyDictionary<string, string> metadata, string key, out double value)
    {
        if (metadata.TryGetValue(key, out var text) &&
            double.TryParse(text, out value))
        {
            return true;
        }

        value = 0.0;
        return false;
    }
}
