using MyAgents.Core.Memory;
using MyAgents.Memory;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class MemoryTests
{
    [Test]
    public void BM25Index_indexes_documents_and_ranks_by_relevance()
    {
        var index = new BM25Index();
        index.AddOrUpdate("doc1", "The quick brown fox jumps over the lazy dog");
        index.AddOrUpdate("doc2", "Brown foxes are known to jump over dogs in the wild");
        index.AddOrUpdate("doc3", "Unrelated document about cats and birds");

        Assert.That(index.DocumentCount, Is.EqualTo(3));

        var hits = index.Search("brown fox jumps", maxResults: 10);
        Assert.That(hits, Has.Count.GreaterThanOrEqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(hits[0].DocId, Is.EqualTo("doc1"));
            Assert.That(hits[1].DocId, Is.EqualTo("doc2"));
            Assert.That(hits.Any(h => h.DocId == "doc3"), Is.False);
        });

        // Removal
        Assert.That(index.Remove("doc1"), Is.True);
        Assert.That(index.DocumentCount, Is.EqualTo(2));
        var hitsAfterRemoval = index.Search("brown fox jumps", maxResults: 10);
        Assert.That(hitsAfterRemoval.Any(h => h.DocId == "doc1"), Is.False);

        index.Clear();
        Assert.That(index.DocumentCount, Is.Zero);
    }

    [Test]
    public async Task HybridMemoryRetriever_performs_sparse_search_and_metadata_filtering()
    {
        var store = new InMemoryMemoryStore();
        await store.SetAsync(new MemoryEntry(
            Key: "mem1",
            Namespace: "user-ns",
            Content: "Authentication service configuration and API keys",
            Metadata: new Dictionary<string, string> { ["env"] = "production" }));

        await store.SetAsync(new MemoryEntry(
            Key: "mem2",
            Namespace: "user-ns",
            Content: "Authentication service setup for development environment",
            Metadata: new Dictionary<string, string> { ["env"] = "development" }));

        var retriever = new HybridMemoryRetriever(store);

        // Search with metadata filter for production only
        var query = new HybridMemoryQuery(
            QueryText: "authentication service",
            MaxResults: 5,
            MetadataFilters: new Dictionary<string, string> { ["env"] = "production" });

        var results = await retriever.SearchHybridAsync("user-ns", query);

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Entry.Key, Is.EqualTo("mem1"));
            Assert.That(results[0].FusedScore, Is.GreaterThan(0));
        });
    }

    [Test]
    public async Task EpisodicMemoryStore_saves_recalls_and_consolidates_episodes()
    {
        var store = new InMemoryMemoryStore();
        var retriever = new HybridMemoryRetriever(store);
        var episodicStore = new EpisodicMemoryStore(store, retriever, "test-episodes");

        var pastTime = DateTimeOffset.UtcNow.AddDays(-10);
        var ep1 = new EpisodicMemoryEntry(
            EpisodeId: "ep-1",
            SessionId: "session-abc",
            Summary: "Discussed database migration to PostgreSQL",
            KeyFacts: ["database: PostgreSQL", "migration: planned"],
            Entities: ["PostgreSQL", "Database"],
            Timestamp: pastTime,
            Salience: 0.9);

        var ep2 = new EpisodicMemoryEntry(
            EpisodeId: "ep-2",
            SessionId: "session-abc",
            Summary: "Finalized schema and indexing for PostgreSQL",
            KeyFacts: ["schema: completed", "indexes: added"],
            Entities: ["PostgreSQL", "Schema"],
            Timestamp: pastTime.AddHours(2),
            Salience: 0.85);

        await episodicStore.SaveEpisodeAsync(ep1);
        await episodicStore.SaveEpisodeAsync(ep2);

        // Recall episodes
        var recallQuery = new EpisodicRecallQuery(
            Query: "PostgreSQL database migration",
            SessionIdFilter: "session-abc",
            MinSalience: 0.8);

        var recalled = await episodicStore.RecallEpisodesAsync(recallQuery);
        Assert.That(recalled, Has.Count.GreaterThanOrEqualTo(1));

        // Consolidate episodes older than 5 days
        await episodicStore.ConsolidateEpisodesAsync("session-abc", olderThan: TimeSpan.FromDays(5));

        var remainingEpisodes = await episodicStore.GetSessionEpisodesAsync("session-abc");
        Assert.Multiple(() =>
        {
            // The two granular episodes were merged into 1 consolidated episode
            Assert.That(remainingEpisodes, Has.Count.EqualTo(1));
            Assert.That(remainingEpisodes[0].EpisodeId, Does.StartWith("consolidated-"));
            Assert.That(remainingEpisodes[0].KeyFacts, Does.Contain("database: PostgreSQL"));
            Assert.That(remainingEpisodes[0].KeyFacts, Does.Contain("schema: completed"));
            Assert.That(remainingEpisodes[0].Entities, Does.Contain("PostgreSQL"));
        });
    }
}
