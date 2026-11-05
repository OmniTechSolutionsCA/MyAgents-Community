using MyAgents.Core.Memory;
using MyAgents.Memory;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class HybridMemoryRetrieverIndexTests
{
    [Test]
    public async Task SearchHybridAsync_indexes_entries_beyond_the_first_500_store_results()
    {
        var store = new OrderedMemoryStore();
        for (var index = 0; index < 600; index++)
        {
            var content = index == 550
                ? "rarelexicalterm550 appears only in this memory entry"
                : $"ordinary memory entry number {index}";
            await store.SetAsync(new MemoryEntry($"entry-{index:D3}", "large", content));
        }

        var retriever = new HybridMemoryRetriever(store);
        var results = await retriever.SearchHybridAsync(
            "large",
            new HybridMemoryQuery("rarelexicalterm550", MaxResults: 1));

        Assert.That(results.Select(static result => result.Entry.Key), Does.Contain("entry-550"));
    }

    [Test]
    public async Task SearchHybridAsync_does_not_scan_store_when_entries_are_already_indexed()
    {
        var store = new OrderedMemoryStore();
        var entry = new MemoryEntry("manual-entry", "manual", "distinctive manually indexed content");
        await store.SetAsync(entry);
        var retriever = new HybridMemoryRetriever(store);
        retriever.IndexLexicalEntry("manual", entry);

        var results = await retriever.SearchHybridAsync(
            "manual",
            new HybridMemoryQuery("distinctive manually indexed", MaxResults: 1));

        Assert.Multiple(() =>
        {
            Assert.That(results.Select(static result => result.Entry.Key), Does.Contain("manual-entry"));
            Assert.That(store.SearchCallCount, Is.Zero);
        });
    }

    [Test]
    public async Task RefreshLexicalIndexAsync_reconciles_added_and_removed_store_entries()
    {
        var store = new OrderedMemoryStore();
        var oldEntry = new MemoryEntry("old-entry", "refresh", "obsoletelexicalmarker content");
        await store.SetAsync(oldEntry);
        var retriever = new HybridMemoryRetriever(store);

        var initialResults = await retriever.SearchHybridAsync(
            "refresh",
            new HybridMemoryQuery("obsoletelexicalmarker", MaxResults: 1));
        Assert.That(initialResults, Has.Count.EqualTo(1));

        await store.DeleteAsync("refresh", oldEntry.Key);
        await store.SetAsync(new MemoryEntry("new-entry", "refresh", "currentlexicalmarker content"));
        await retriever.RefreshLexicalIndexAsync("refresh");

        var addedResults = await retriever.SearchHybridAsync(
            "refresh",
            new HybridMemoryQuery("currentlexicalmarker", MaxResults: 1));
        var removedResults = await retriever.SearchHybridAsync(
            "refresh",
            new HybridMemoryQuery("obsoletelexicalmarker", MaxResults: 1));

        Assert.Multiple(() =>
        {
            Assert.That(addedResults.Select(static result => result.Entry.Key), Does.Contain("new-entry"));
            Assert.That(removedResults, Is.Empty);
        });
    }

    [Test]
    public async Task RefreshLexicalIndexAsync_removes_entries_deleted_by_retention()
    {
        var store = new OrderedMemoryStore();
        var expiredEntry = new MemoryEntry(
            "expired-entry",
            "retention",
            "retentiononlylexicalmarker",
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-2));
        await store.SetAsync(expiredEntry);
        await store.SetAsync(new MemoryEntry("current-entry", "retention", "current retention content"));
        var retriever = new HybridMemoryRetriever(store);

        var initialResults = await retriever.SearchHybridAsync(
            "retention",
            new HybridMemoryQuery("retentiononlylexicalmarker", MaxResults: 1));
        Assert.That(initialResults, Has.Count.EqualTo(1));

        var retentionService = new MemoryRetentionService(store);
        var retentionResult = await retentionService.ApplyAsync(
            "retention",
            new MemoryRetentionPolicy(MaxEntries: 10, MaxAge: TimeSpan.FromDays(1)));
        await retriever.RefreshLexicalIndexAsync("retention");

        var results = await retriever.SearchHybridAsync(
            "retention",
            new HybridMemoryQuery("retentiononlylexicalmarker", MaxResults: 1));

        Assert.Multiple(() =>
        {
            Assert.That(retentionResult.DeletedCount, Is.EqualTo(1));
            Assert.That(results, Is.Empty);
        });
    }

    private sealed class OrderedMemoryStore : IMemoryStore
    {
        private readonly Dictionary<string, List<MemoryEntry>> _namespaces = new(StringComparer.Ordinal);

        public int SearchCallCount { get; private set; }

        public ValueTask SetAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
        {
            if (!_namespaces.TryGetValue(entry.Namespace, out var entries))
            {
                entries = [];
                _namespaces.Add(entry.Namespace, entries);
            }

            var existingIndex = entries.FindIndex(existing => existing.Key == entry.Key);
            if (existingIndex >= 0)
                entries[existingIndex] = entry;
            else
                entries.Add(entry);

            return ValueTask.CompletedTask;
        }

        public ValueTask<MemoryEntry?> GetAsync(
            string ns,
            string key,
            CancellationToken cancellationToken = default)
        {
            var entry = _namespaces.TryGetValue(ns, out var entries)
                ? entries.FirstOrDefault(candidate => candidate.Key == key)
                : null;
            return ValueTask.FromResult(entry);
        }

        public ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(
            string ns,
            string? query = null,
            int maxResults = 10,
            CancellationToken cancellationToken = default)
        {
            SearchCallCount++;
            IReadOnlyList<MemoryEntry> results = _namespaces.TryGetValue(ns, out var entries)
                ? entries
                    .Where(entry => string.IsNullOrEmpty(query)
                        || entry.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Take(maxResults)
                    .ToArray()
                : Array.Empty<MemoryEntry>();
            return ValueTask.FromResult(results);
        }

        public ValueTask DeleteAsync(string ns, string key, CancellationToken cancellationToken = default)
        {
            if (_namespaces.TryGetValue(ns, out var entries))
                entries.RemoveAll(entry => entry.Key == key);
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync(string ns, CancellationToken cancellationToken = default)
        {
            _namespaces.Remove(ns);
            return ValueTask.CompletedTask;
        }
    }
}
