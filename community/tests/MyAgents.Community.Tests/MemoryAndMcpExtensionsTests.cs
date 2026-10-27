using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using MyAgents.Core;
using MyAgents.Core.Memory;
using MyAgents.Mcp;
using MyAgents.Memory;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class MemoryAndMcpExtensionsTests
{
    private sealed class MockSummaryGenerator : IMemorySummaryGenerator
    {
        public ValueTask<string> GenerateAsync(string @namespace, IReadOnlyList<MemoryEntry> entries, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult($"Summary of {entries.Count} entries in {@namespace}");
        }
    }

    private sealed class MockMemoryRetriever : IMemoryRetriever
    {
        public ValueTask<IReadOnlyList<MemorySearchResult>> SearchAsync(string @namespace, MemoryQuery query, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<MemorySearchResult> results =
            [
                new MemorySearchResult(
                    Entry: new MemoryEntry("doc1", @namespace, "Relevant architecture documentation."),
                    Score: 0.95,
                    SemanticScore: 0.95,
                    KeywordScore: 0.95,
                    RecencyScore: 1.0,
                    ImportanceScore: 1.0)
            ];
            return ValueTask.FromResult(results);
        }
    }

    private sealed class MockEpisodicStore : IEpisodicMemoryStore
    {
        public List<EpisodicMemoryEntry> SavedEpisodes { get; } = [];

        public ValueTask<IReadOnlyList<EpisodicMemoryEntry>> RecallEpisodesAsync(EpisodicRecallQuery query, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<EpisodicMemoryEntry> list =
            [
                new EpisodicMemoryEntry(
                    EpisodeId: "ep-1",
                    SessionId: query.SessionIdFilter ?? "sess-any",
                    Summary: $"Episode regarding {query.Query}",
                    KeyFacts: ["User prefers C# over Python"],
                    Entities: ["Project Alpha"],
                    Timestamp: DateTimeOffset.UtcNow,
                    Salience: 0.9)
            ];
            return ValueTask.FromResult(list);
        }

        public ValueTask SaveEpisodeAsync(EpisodicMemoryEntry episode, CancellationToken cancellationToken = default)
        {
            SavedEpisodes.Add(episode);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<EpisodicMemoryEntry>> GetSessionEpisodesAsync(string sessionId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<EpisodicMemoryEntry>>(SavedEpisodes.Where(e => e.SessionId == sessionId).ToArray());

        public ValueTask ConsolidateEpisodesAsync(string sessionId, TimeSpan olderThan, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class MockHotReloadManager : IMcpServerHotReloadManager
    {
        public event EventHandler<McpServerReloadResult>? OnServerReloaded
        {
            add { }
            remove { }
        }

        public IReadOnlyList<McpServerInfo> GetActiveServers() =>
        [
            new McpServerInfo("server-1", McpServerStatus.Connected, 4, ["tool-a", "tool-b"], DateTimeOffset.UtcNow)
        ];

        public ValueTask<McpServerReloadResult> ReloadServerAsync(string serverName, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new McpServerReloadResult(serverName, Success: true, PreviousToolCount: 4, NewToolCount: 5, AddedTools: ["tool-c"], RemovedTools: []));

        public ValueTask<IReadOnlyList<McpServerReloadResult>> ReloadAllAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<McpServerReloadResult>>([
                new McpServerReloadResult("server-1", Success: true, PreviousToolCount: 4, NewToolCount: 5, AddedTools: ["tool-c"], RemovedTools: [])
            ]);

        public ValueTask<bool> RegisterServerAsync(string name, IClientTransport transport, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public ValueTask<bool> UnregisterServerAsync(string name, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MockSkillDiscovery : ISkillDiscoveryService
    {
        public ValueTask<IReadOnlyList<DiscoveredSkill>> DiscoverSkillsAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<DiscoveredSkill> manifests =
            [
                new DiscoveredSkill("git-tools", "Git CLI tools", directoryPath, ["git", "vcs"])
            ];
            return ValueTask.FromResult(manifests);
        }
    }

    private sealed class EchoAgent : IAgent
    {
        public AgentTurnResult LastResult { get; private set; } = default!;

        public ValueTask<AgentTurnResult> InvokeAsync(AgentTurnContext context, CancellationToken cancellationToken = default)
        {
            LastResult = new AgentTurnResult(
                [new AgentMessage("assistant", "Echo reply")],
                new AgentTurnTelemetry(TimeSpan.FromMilliseconds(5)));
            return ValueTask.FromResult(LastResult);
        }

        public async IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
            AgentTurnContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return await InvokeAsync(context, cancellationToken);
        }

        public void Dispose() { }
    }

    [Test]
    public async Task MemoryRetentionService_DeletesExpiredAndSummarizesOverflow()
    {
        var store = new InMemoryMemoryStore();
        var ns = "tenant-retention";

        var oldEntry = new MemoryEntry("old", ns, "Old memory", CreatedAt: DateTimeOffset.UtcNow.AddDays(-10));
        var freshEntry1 = new MemoryEntry("fresh1", ns, "Fresh memory 1", CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var freshEntry2 = new MemoryEntry("fresh2", ns, "Fresh memory 2", CreatedAt: DateTimeOffset.UtcNow);

        await store.SetAsync(oldEntry);
        await store.SetAsync(freshEntry1);
        await store.SetAsync(freshEntry2);

        var retentionService = new MemoryRetentionService(store, new MockSummaryGenerator());

        // Policy: max age 1 day, max entries 1, summarize overflow
        var policy = new MemoryRetentionPolicy(
            MaxAge: TimeSpan.FromDays(1),
            MaxEntries: 1,
            SummarizeTrimmedEntries: true);

        var result = await retentionService.ApplyAsync(ns, policy);

        Assert.That(result.DeletedCount, Is.GreaterThanOrEqualTo(2));
        Assert.That(result.SummaryEntry, Is.Not.Null);
        Assert.That(result.SummaryEntry!.Content, Contains.Substring("Summary of"));

        // Verify remaining items in store
        var search = await store.SearchAsync(ns);
        Assert.That(search.Any(e => e.Key == "old"), Is.False);
    }

    [Test]
    public async Task RetrievalAugmentationFeature_InjectsRetrievedContextIntoMessages()
    {
        var feature = new RetrievalAugmentationFeature(new RetrievalAugmentationOptions("kb-namespace"));
        var assembly = new AgentAssembly();
        feature.Contribute(assembly);

        var pipeline = assembly.Pipeline.Build();

        var services = new ServiceCollection()
            .AddSingleton<IMemoryRetriever>(new MockMemoryRetriever())
            .BuildServiceProvider();

        IReadOnlyList<AgentMessage>? capturedMessages = null;

        var context = new AgentTurnContext(
            SessionId: "session-rag-1",
            Messages:
            [
                new AgentMessage("system", "You are a helpful assistant."),
                new AgentMessage("user", "Explain the architecture")
            ],
            Services: new AgentTurnServices(services));

        using var innerAgent = new DelegateAgent(ctx =>
        {
            capturedMessages = ctx.Messages;
            return new AgentTurnResult([new AgentMessage("assistant", "OK")], new AgentTurnTelemetry(TimeSpan.Zero));
        });

        await pipeline.InvokeAsync(innerAgent, context);

        Assert.That(capturedMessages, Is.Not.Null);
        Assert.That(capturedMessages!.Count, Is.EqualTo(3)); // system + RAG system + user
        Assert.That(capturedMessages.Any(m => m.Content.Contains("Retrieved context:")), Is.True);
        Assert.That(capturedMessages.Any(m => m.Content.Contains("Relevant architecture documentation.")), Is.True);
    }

    [Test]
    public async Task EpisodicMemoryTools_RecallAndSaveEpisodes()
    {
        var episodicStore = new MockEpisodicStore();

        // 1. RecallEpisodicMemoriesTool
        var recallTool = new RecallEpisodicMemoriesTool(episodicStore);
        var recallJson = await recallTool.InvokeAsync(JsonSerializer.Serialize(new
        {
            query = "preferences",
            max_episodes = 3
        }));

        Assert.That(recallJson, Contains.Substring("success"));
        Assert.That(recallJson, Contains.Substring("User prefers C# over Python"));

        // 2. SaveEpisodicFactTool
        var saveTool = new SaveEpisodicFactTool(episodicStore);
        var saveJson = await saveTool.InvokeAsync(JsonSerializer.Serialize(new
        {
            session_id = "sess-100",
            summary = "Decided on SQLite for checkpointer",
            key_facts = new[] { "Storage is SQLite" },
            entities = new[] { "SqliteCheckpointer" },
            salience = 0.95
        }));

        Assert.That(saveJson, Contains.Substring("success"));
        Assert.That(episodicStore.SavedEpisodes.Count, Is.EqualTo(1));
        Assert.That(episodicStore.SavedEpisodes[0].Summary, Is.EqualTo("Decided on SQLite for checkpointer"));
    }

    [Test]
    public async Task McpHotReloadTools_ListReloadAndDiscover()
    {
        var manager = new MockHotReloadManager();
        var discovery = new MockSkillDiscovery();

        // 1. ListActiveMcpServersTool
        var listTool = new ListActiveMcpServersTool(manager);
        var listJson = await listTool.InvokeAsync("{}");
        Assert.That(listJson, Contains.Substring("server-1"));
        Assert.That(listJson, Contains.Substring("\"Status\": 0"));

        // 2. ReloadMcpServerTool
        var reloadTool = new ReloadMcpServerTool(manager);
        var reloadSingle = await reloadTool.InvokeAsync("{\"server_name\":\"server-1\"}");
        Assert.That(reloadSingle, Contains.Substring("server-1"));
        Assert.That(reloadSingle, Contains.Substring("NewToolCount"));

        var reloadAll = await reloadTool.InvokeAsync("{}");
        Assert.That(reloadAll, Contains.Substring("server-1"));

        // 3. DiscoverSkillsTool
        var discoverTool = new DiscoverSkillsTool(discovery);
        var discoverJson = await discoverTool.InvokeAsync("{\"directory_path\":\"/skills\"}");
        Assert.That(discoverJson, Contains.Substring("git-tools"));
    }

    private sealed class DelegateAgent(Func<AgentTurnContext, AgentTurnResult> handler) : IAgent
    {
        public ValueTask<AgentTurnResult> InvokeAsync(AgentTurnContext context, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(handler(context));
        }

        public async IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
            AgentTurnContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return await InvokeAsync(context, cancellationToken);
        }

        public void Dispose() { }
    }
}
