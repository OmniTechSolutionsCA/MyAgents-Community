# Episodic Memory & Retention Policies

While standard session state is tied to a single conversation, real-world agents require **long-term episodic memory** to remember user preferences, architectural decisions, and domain milestones across separate sessions and days.

## What is Episodic Memory?

`MyAgents.Memory` provides `IEpisodicMemoryStore` (`EpisodicMemoryStore`), a structured long-term memory layer built on top of `IMemoryStore` and `IHybridMemoryRetriever`:
1. **Structured Episodes (`EpisodicMemoryEntry`)**: Stores `EpisodeId`, `SessionId`, `Summary`, `KeyFacts`, `Entities`, `Timestamp`, and `Salience` (0.0 to 1.0).
2. **Cross-Session Hybrid Recall (`RecallEpisodesAsync`)**: Searches past episodes using hybrid semantic + BM25 retrieval with optional session, age, and minimum salience filters.
3. **Episode Consolidation (`ConsolidateEpisodesAsync`)**: Merges older, lower-salience episodes for a session into consolidated milestone entries.
4. **Retention & Summarization (`MemoryRetentionService`)**: Enforces entry count and age limits while optionally summarizing trimmed entries and updating the BM25 lexical index.

---

## 1. Setting up Episodic Memory

```csharp
using Microsoft.Extensions.DependencyInjection;
using MyAgents.Core.Memory;
using MyAgents.Memory;

var services = new ServiceCollection();
services.AddSingleton<IMemoryStore, InMemoryMemoryStore>();

// Registers IEpisodicMemoryStore, HybridMemoryRetriever, and EpisodicMemoryFeature
services.AddEpisodicMemory(ns: "user-longterm-memory");

await using var provider = services.BuildServiceProvider();
var episodicStore = provider.GetRequiredService<IEpisodicMemoryStore>();
```

---

## 2. Saving, Recalling & Consolidating Episodes Programmatically

```csharp
// 1. Save an episode
var episode = new EpisodicMemoryEntry(
    EpisodeId: "ep-001",
    SessionId: "session-42",
    Summary: "User prefers NativeAOT deployments on Linux containers with SQLite WAL persistence.",
    KeyFacts: ["Target runtime: .NET 10 NativeAOT", "Database: SQLite WAL mode"],
    Entities: ["NativeAOT", "SQLite"],
    Timestamp: DateTimeOffset.UtcNow,
    Salience: 0.95);

await episodicStore.SaveEpisodeAsync(episode, cancellationToken);

// 2. Recall episodes across sessions
var query = new EpisodicRecallQuery(
    Query: "deployment database preference",
    SessionIdFilter: null,
    MaxEpisodes: 5,
    MinSalience: 0.5,
    MaxAge: TimeSpan.FromDays(90));

IReadOnlyList<EpisodicMemoryEntry> recalled = await episodicStore.RecallEpisodesAsync(query, cancellationToken);

// 3. Consolidate older episodes for a session into a single milestone
await episodicStore.ConsolidateEpisodesAsync(
    sessionId: "session-42",
    olderThan: TimeSpan.FromDays(7),
    cancellationToken: cancellationToken);
```

---

## 3. Equipping an Agent with Episodic Memory Tools (`EpisodicMemoryFeature`)

Register `EpisodicMemoryFeature` on an `AgentDefinition` to give the model autonomous tools to record and search episodic memories:

```csharp
var episodicFeature = provider.GetRequiredService<EpisodicMemoryFeature>();

var definition = new AgentDefinition("personal-architect", [
    episodicFeature
]);
```

`EpisodicMemoryFeature` registers two tools:
- `save_episodic_fact`: Persists an important user fact, preference, or milestone summary (`session_id`, `summary`, `key_facts`, `entities`, `salience`).
- `recall_past_episodes`: Searches long-term episodic memory for relevant past conversations (`query`, `session_id`, `max_episodes`).

---

## 4. Memory Retention Policies (`MemoryRetentionService`)

Use `MemoryRetentionService` to prune expired or excess memory entries while optionally generating milestone summaries via `IMemorySummaryGenerator`:

```csharp
var retentionService = provider.GetRequiredService<MemoryRetentionService>();

var policy = new MemoryRetentionPolicy(
    MaxEntries: 500,
    MaxAge: TimeSpan.FromDays(90),
    SummarizeTrimmedEntries: true,
    SummaryKeyPrefix: "milestone-summary",
    SummarySourceCount: 20);

MemoryRetentionResult result = await retentionService.ApplyAsync(
    ns: "user-longterm-memory",
    policy: policy,
    cancellationToken: cancellationToken);

Console.WriteLine($"Pruned {result.DeletedCount} entries, summarized {result.SummarizedCount} entries.");
```
