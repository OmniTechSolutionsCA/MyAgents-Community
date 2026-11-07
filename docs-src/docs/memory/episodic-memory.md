# Episodic Memory & Long-Term Recall

While standard session state is ephemeral, real-world agents require **long-term episodic memory** to remember user preferences, previous decisions, and domain facts across different sessions and days.

## What is Episodic Memory?

`MyAgents.Memory` provides `IEpisodicMemoryStore`, a high-level memory layer built on top of hybrid retrieval:
1. **Semantic Persistence**: Automatically indexes conversational facts into a persistent store.
2. **Cross-Session Recall**: Queries relevant memories based on conversational context.
3. **Automated Milestone Consolidation**: Summarizes multiple related memories into concise milestone entries.
4. **Retention Policies**: Trims expired or low-importance memories to respect token budgets.

---

## 1. Setting up Episodic Memory

```csharp
using Microsoft.Extensions.DependencyInjection;
using MyAgents.Memory;

var services = new ServiceCollection();

// Registers IEpisodicMemoryStore and EpisodicMemoryFeature
services.AddEpisodicMemory(ns: "user-longterm-memory");

var provider = services.BuildServiceProvider();
var episodicStore = provider.GetRequiredService<IEpisodicMemoryStore>();
```

---

## 2. Using the Episodic Memory Feature in an Agent

Register `EpisodicMemoryFeature` in your agent definition to give the model tools to remember and recall information autonomously:

```csharp
var episodicFeature = provider.GetRequiredService<EpisodicMemoryFeature>();

var definition = new AgentDefinition("personal-assistant", [
    episodicFeature
]);
```

The feature automatically injects two tools into the model:
- `remember(key, content, importance)`: Stores an episodic observation.
- `recall(query, maxResults)`: Searches episodic memory for relevant facts.

---

## 3. Retention Policies & Summarization

Over months of usage, memories accumulate. Use `MemoryRetentionService` to prune older memories while preserving summaries:

```csharp
var retentionService = provider.GetRequiredService<MemoryRetentionService>();

var policy = new MemoryRetentionPolicy(
    MaxEntries: 500,
    MaxAge: TimeSpan.FromDays(90),
    SummarizeTrimmedEntries: true,
    SummarySourceCount: 20,
    SummaryKeyPrefix: "milestone-summary"
);

MemoryRetentionResult result = await retentionService.ApplyAsync(
    ns: "user-longterm-memory",
    policy: policy,
    cancellationToken: cancellationToken);

Console.WriteLine($"Pruned {result.DeletedCount} entries, summarized {result.SummarizedCount} milestones.");
```
