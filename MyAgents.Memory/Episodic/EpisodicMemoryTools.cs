using System.Text.Json;
using MyAgents.Core;
using MyAgents.Core.Tools;

namespace MyAgents.Memory;

/// <summary>
/// Tool that allows an agent to search and recall past long-term episodic memories and interaction history.
/// </summary>
public sealed class RecallEpisodicMemoriesTool : ITool
{
    private readonly IEpisodicMemoryStore _store;

    public RecallEpisodicMemoriesTool(IEpisodicMemoryStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string Name => "recall_past_episodes";

    public string? Description => "Searches long-term episodic memory for relevant past conversations, decisions, and established user facts across sessions.";

    public string? ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "Natural language search query to locate relevant past episodes."
            },
            "session_id": {
              "type": "string",
              "description": "Optional session ID filter to limit recall to a specific conversation."
            },
            "max_episodes": {
              "type": "integer",
              "description": "Maximum number of past episodes to retrieve (default: 5)."
            }
          },
          "required": ["query"]
        }
        """;

    public async ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonArguments);
            var queryText = doc.RootElement.GetProperty("query").GetString();
            if (string.IsNullOrWhiteSpace(queryText))
            {
                return JsonSerializer.Serialize(new { status = "error", error = "query parameter is required." });
            }

            var sessionId = doc.RootElement.TryGetProperty("session_id", out var sidProp) ? sidProp.GetString() : null;
            var maxEpisodes = doc.RootElement.TryGetProperty("max_episodes", out var maxProp) && maxProp.TryGetInt32(out var m) ? m : 5;

            var recallQuery = new EpisodicRecallQuery(
                Query: queryText,
                SessionIdFilter: sessionId,
                MaxEpisodes: maxEpisodes);

            var episodes = await _store.RecallEpisodesAsync(recallQuery, cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Serialize(new
            {
                status = "success",
                count = episodes.Count,
                episodes = episodes.Select(e => new
                {
                    episode_id = e.EpisodeId,
                    session_id = e.SessionId,
                    summary = e.Summary,
                    key_facts = e.KeyFacts,
                    entities = e.Entities,
                    timestamp = e.Timestamp,
                    salience = e.Salience
                })
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { status = "error", error = ex.Message });
        }
    }
}

/// <summary>
/// Tool that allows an agent to explicitly record a crucial fact, decision, or milestone to long-term episodic memory.
/// </summary>
public sealed class SaveEpisodicFactTool : ITool
{
    private readonly IEpisodicMemoryStore _store;

    public SaveEpisodicFactTool(IEpisodicMemoryStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string Name => "save_episodic_fact";

    public string? Description => "Persists an important user fact, preference, architectural decision, or episode summary into long-term episodic memory.";

    public string? ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "session_id": {
              "type": "string",
              "description": "The current or originating session ID."
            },
            "summary": {
              "type": "string",
              "description": "Distilled summary of the episode or milestone."
            },
            "key_facts": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Extracted key facts or preferences."
            },
            "entities": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Named entities, project names, or key symbols."
            },
            "salience": {
              "type": "number",
              "description": "Importance weight from 0.0 to 1.0 (default: 1.0)."
            }
          },
          "required": ["session_id", "summary"]
        }
        """;

    public async ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonArguments);
            var sessionId = doc.RootElement.GetProperty("session_id").GetString();
            var summary = doc.RootElement.GetProperty("summary").GetString();

            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(summary))
            {
                return JsonSerializer.Serialize(new { status = "error", error = "session_id and summary are required." });
            }

            var facts = new List<string>();
            if (doc.RootElement.TryGetProperty("key_facts", out var factsArr) && factsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in factsArr.EnumerateArray())
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) facts.Add(s);
                }
            }

            var entities = new List<string>();
            if (doc.RootElement.TryGetProperty("entities", out var entArr) && entArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in entArr.EnumerateArray())
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) entities.Add(s);
                }
            }

            var salience = doc.RootElement.TryGetProperty("salience", out var salProp) && salProp.TryGetDouble(out var sal) ? sal : 1.0;

            var episode = new EpisodicMemoryEntry(
                EpisodeId: $"ep-{Guid.NewGuid():N}",
                SessionId: sessionId,
                Summary: summary,
                KeyFacts: facts,
                Entities: entities,
                Timestamp: DateTimeOffset.UtcNow,
                Salience: salience);

            await _store.SaveEpisodeAsync(episode, cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Serialize(new
            {
                status = "success",
                episode_id = episode.EpisodeId,
                session_id = episode.SessionId
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { status = "error", error = ex.Message });
        }
    }
}

/// <summary>
/// Feature that registers long-term episodic memory tools (<c>recall_past_episodes</c>, <c>save_episodic_fact</c>)
/// into an agent assembly.
/// </summary>
public sealed class EpisodicMemoryFeature : IAgentFeature
{
    private readonly IEpisodicMemoryStore _store;

    public EpisodicMemoryFeature(IEpisodicMemoryStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public void Contribute(AgentAssembly assembly)
    {
        assembly.Tools.Add(new RecallEpisodicMemoriesTool(_store));
        assembly.Tools.Add(new SaveEpisodicFactTool(_store));
        assembly.Capabilities.Add(AgentCapabilities.Memory);
        assembly.Capabilities.Add(AgentCapabilities.Tools);
    }
}