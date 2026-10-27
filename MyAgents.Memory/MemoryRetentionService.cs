using MyAgents.Core.Memory;

namespace MyAgents.Memory;

/// <summary>
/// Applies retention and summarization policies over a memory namespace.
/// </summary>
public sealed class MemoryRetentionService
{
    private readonly IMemoryStore _store;
    private readonly IMemorySummaryGenerator? _summaryGenerator;

    public MemoryRetentionService(IMemoryStore store, IMemorySummaryGenerator? summaryGenerator = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _summaryGenerator = summaryGenerator;
    }

    public async ValueTask<MemoryRetentionResult> ApplyAsync(
        string ns,
        MemoryRetentionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ns);
        ArgumentNullException.ThrowIfNull(policy);

        var allEntries = (await _store.SearchAsync(ns, maxResults: int.MaxValue, cancellationToken: cancellationToken).ConfigureAwait(false))
            .OrderByDescending(static entry => entry.CreatedAt)
            .ToList();

        var now = DateTimeOffset.UtcNow;
        var expired = policy.MaxAge is null
            ? []
            : allEntries.Where(entry => entry.CreatedAt is not null && now - entry.CreatedAt > policy.MaxAge.Value).ToList();

        var survivors = allEntries.Except(expired).ToList();
        var overflow = survivors.Count > policy.MaxEntries
            ? survivors.Skip(policy.MaxEntries).ToList()
            : [];

        var toDelete = expired
            .Concat(overflow)
            .GroupBy(static entry => (entry.Namespace, entry.Key))
            .Select(static group => group.First())
            .ToArray();

        foreach (var entry in toDelete)
            await _store.DeleteAsync(entry.Namespace, entry.Key, cancellationToken).ConfigureAwait(false);

        MemoryEntry? summaryEntry = null;
        if (policy.SummarizeTrimmedEntries && _summaryGenerator is not null && overflow.Count > 0)
        {
            var summarized = overflow
                .OrderByDescending(static entry => entry.CreatedAt)
                .Take(policy.SummarySourceCount)
                .ToArray();
            var summary = await _summaryGenerator.GenerateAsync(ns, summarized, cancellationToken).ConfigureAwait(false);
            summaryEntry = new MemoryEntry(
                Key: $"{policy.SummaryKeyPrefix}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
                Namespace: ns,
                Content: summary,
                Metadata: new Dictionary<string, string>
                {
                    ["kind"] = "summary",
                    ["sourceCount"] = summarized.Length.ToString(),
                },
                CreatedAt: DateTimeOffset.UtcNow);
            await _store.SetAsync(summaryEntry, cancellationToken).ConfigureAwait(false);
        }

        return new MemoryRetentionResult(
            DeletedCount: toDelete.Length,
            SummarizedCount: overflow.Count,
            SummaryEntry: summaryEntry);
    }
}
