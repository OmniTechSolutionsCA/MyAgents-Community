using System.Globalization;
using System.Text;
using MyAgents.Core;
using MyAgents.Core.Memory;

namespace MyAgents.Memory;

/// <summary>
/// Options for retrieval augmentation.
/// </summary>
public sealed record RetrievalAugmentationOptions(
    string Namespace,
    int MaxResults = 5,
    double MinScore = 0.0,
    IReadOnlyDictionary<string, string>? MetadataFilters = null,
    string? Instruction = null,
    int? MaxContextCharacters = null,
    bool DeduplicateAcrossTurns = false,
    int? MaxContextTokens = null,
    int AdjacentChunkWindow = 0,
    Action<CitationGroundingResult>? OnCitationsEvaluated = null,
    bool AppendCitationReferences = false);

internal sealed class RetrievalAugmentationMiddleware : IAgentMiddleware
{
    private readonly RetrievalAugmentationOptions _options;

    public RetrievalAugmentationMiddleware(RetrievalAugmentationOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.Namespace);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxResults, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.AdjacentChunkWindow);
        if (_options.MaxContextCharacters is { } maxChars)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);
        }
        if (_options.MaxContextTokens is { } maxTokens)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, 1);
        }
    }

    public async ValueTask<AgentTurnResult> InvokeAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>> next,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var (augmentedContext, injectedResults) = await AugmentContextAsync(context, cancellationToken).ConfigureAwait(false);
        var turnResult = await next(augmentedContext, cancellationToken).ConfigureAwait(false);
        return EvaluateTurnCitations(turnResult, injectedResults);
    }

    public IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, IAsyncEnumerable<AgentTurnResult>> next,
        CancellationToken cancellationToken = default)
        => InvokeStreamingCoreAsync(context, next, cancellationToken);

    private async IAsyncEnumerable<AgentTurnResult> InvokeStreamingCoreAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, IAsyncEnumerable<AgentTurnResult>> next,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (augmentedContext, _) = await AugmentContextAsync(context, cancellationToken).ConfigureAwait(false);
        await foreach (var chunk in next(augmentedContext, cancellationToken).ConfigureAwait(false))
            yield return chunk;
    }

    private async ValueTask<(AgentTurnContext Context, List<MemorySearchResult> InjectedResults)> AugmentContextAsync(
        AgentTurnContext context,
        CancellationToken cancellationToken)
    {
        var retriever = context.Services.GetService<IMemoryRetriever>();
        var queryText = context.Messages
            .LastOrDefault(static message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(message.Content))
            ?.Content;

        if (retriever is null || string.IsNullOrWhiteSpace(queryText))
            return (context, []);

        var results = await retriever.SearchAsync(
            _options.Namespace,
            new MemoryQuery(
                Text: queryText,
                MaxResults: _options.MaxResults,
                MinScore: _options.MinScore,
                MetadataFilters: _options.MetadataFilters),
            cancellationToken).ConfigureAwait(false);

        if (results.Count == 0)
            return (context, []);

        var tokenCounter = context.Services.GetService<ITokenCounter>() ?? ApproximateTokenCounter.Instance;
        var store = _options.AdjacentChunkWindow > 0 ? context.Services.GetService<IMemoryStore>() : null;
        var filteredResults = await FilterAndBudgetResultsAsync(
            context.Messages,
            results,
            tokenCounter,
            store,
            cancellationToken).ConfigureAwait(false);
        if (filteredResults.Count == 0)
            return (context, []);

        var injectedResults = new List<MemorySearchResult>(filteredResults.Count);
        for (var i = 0; i < filteredResults.Count; i++)
        {
            injectedResults.Add(filteredResults[i].Result);
        }

        var augmentedMessages = InjectRetrievedContext(context.Messages, BuildRetrievedContextMessage(filteredResults));
        return (context with { Messages = augmentedMessages }, injectedResults);
    }

    private async ValueTask<List<(MemorySearchResult Result, string Content)>> FilterAndBudgetResultsAsync(
        IReadOnlyList<AgentMessage> existingMessages,
        IReadOnlyList<MemorySearchResult> results,
        ITokenCounter tokenCounter,
        IMemoryStore? store,
        CancellationToken cancellationToken)
    {
        var priorContextMessages = _options.DeduplicateAcrossTurns
            ? existingMessages
                .Where(static m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)
                    && m.Content is not null
                    && m.Content.StartsWith("Retrieved context:", StringComparison.Ordinal))
                .Select(static m => m.Content!)
                .ToArray()
            : [];

        var accepted = new List<(MemorySearchResult Result, string Content)>(results.Count);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var remainingCharBudget = _options.MaxContextCharacters;
        var remainingTokenBudget = _options.MaxContextTokens;

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var entry = result.Entry;
            if (!seenKeys.Add(entry.Key))
            {
                continue;
            }

            var snippet = store is not null && _options.AdjacentChunkWindow > 0
                ? await ExpandAdjacentChunksAsync(entry, store, seenKeys, cancellationToken).ConfigureAwait(false)
                : entry.Content;

            if (priorContextMessages.Length > 0 && !string.IsNullOrEmpty(snippet))
            {
                var alreadyPresent = false;
                for (var m = 0; m < priorContextMessages.Length; m++)
                {
                    if (priorContextMessages[m].Contains(snippet, StringComparison.Ordinal))
                    {
                        alreadyPresent = true;
                        break;
                    }
                }

                if (alreadyPresent)
                {
                    continue;
                }
            }

            if (remainingCharBudget is { } charBudget)
            {
                if (snippet.Length > charBudget)
                {
                    if (accepted.Count == 0 && charBudget > 0)
                    {
                        snippet = snippet[..charBudget];
                        remainingCharBudget = 0;
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    remainingCharBudget = charBudget - snippet.Length;
                }
            }

            if (remainingTokenBudget is { } tokenBudget)
            {
                var snippetTokens = tokenCounter.CountTokens(snippet);
                if (snippetTokens > tokenBudget)
                {
                    break;
                }

                remainingTokenBudget = tokenBudget - snippetTokens;
            }

            accepted.Add((result, snippet));
        }

        return accepted;
    }

    private async ValueTask<string> ExpandAdjacentChunksAsync(
        MemoryEntry centerEntry,
        IMemoryStore store,
        HashSet<string> seenKeys,
        CancellationToken cancellationToken)
    {
        var metadata = centerEntry.Metadata;
        if (metadata is null ||
            !metadata.TryGetValue(DocumentMetadataKeys.ChunkIndex, out var rawIdx) ||
            !int.TryParse(rawIdx, NumberStyles.Integer, CultureInfo.InvariantCulture, out var centerIdx) ||
            !metadata.TryGetValue(DocumentMetadataKeys.ChunkCount, out var rawCount) ||
            !int.TryParse(rawCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalChunks))
        {
            return centerEntry.Content;
        }

        const string chunkMarker = "#chunk-";
        var markerPos = centerEntry.Key.LastIndexOf(chunkMarker, StringComparison.Ordinal);
        if (markerPos <= 0)
        {
            return centerEntry.Content;
        }

        var docPrefix = centerEntry.Key[..markerPos];
        var minIdx = Math.Max(0, centerIdx - _options.AdjacentChunkWindow);
        var maxIdx = Math.Min(totalChunks - 1, centerIdx + _options.AdjacentChunkWindow);
        if (minIdx >= maxIdx)
        {
            return centerEntry.Content;
        }

        var combined = new StringBuilder();
        string? previousChunk = null;
        for (var idx = minIdx; idx <= maxIdx; idx++)
        {
            string? chunkText;
            if (idx == centerIdx)
            {
                chunkText = centerEntry.Content;
            }
            else
            {
                var neighborKey = $"{docPrefix}{chunkMarker}{idx:D4}";
                seenKeys.Add(neighborKey);
                var neighbor = await store.GetAsync(_options.Namespace, neighborKey, cancellationToken).ConfigureAwait(false);
                chunkText = neighbor?.Content;
            }

            if (!string.IsNullOrEmpty(chunkText))
            {
                AppendDeduplicatingOverlap(combined, previousChunk.AsSpan(), chunkText);
                previousChunk = chunkText;
            }
        }

        return combined.Length > 0 ? combined.ToString() : centerEntry.Content;
    }

    private static void AppendDeduplicatingOverlap(StringBuilder builder, ReadOnlySpan<char> previousSpan, string nextChunk)
    {
        if (previousSpan.IsEmpty)
        {
            builder.Append(nextChunk);
            return;
        }

        var nextSpan = nextChunk.AsSpan();
        var maxOverlap = Math.Min(previousSpan.Length, nextSpan.Length);
        var overlap = 0;
        for (var len = maxOverlap; len >= 1; len--)
        {
            if (previousSpan.Slice(previousSpan.Length - len, len).SequenceEqual(nextSpan.Slice(0, len)))
            {
                overlap = len;
                break;
            }
        }

        if (overlap < nextChunk.Length)
        {
            builder.Append(nextChunk, overlap, nextChunk.Length - overlap);
        }
    }

    private AgentTurnResult EvaluateTurnCitations(
        AgentTurnResult turnResult,
        List<MemorySearchResult> injectedResults)
    {
        if (injectedResults.Count == 0 || turnResult.Output.Count == 0)
        {
            return turnResult;
        }

        var lastAssistantIndex = -1;
        for (var i = turnResult.Output.Count - 1; i >= 0; i--)
        {
            if (string.Equals(turnResult.Output[i].Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(turnResult.Output[i].Content))
            {
                lastAssistantIndex = i;
                break;
            }
        }

        if (lastAssistantIndex < 0)
        {
            return turnResult;
        }

        var assistantMessage = turnResult.Output[lastAssistantIndex];
        var grounding = CitationGrounder.Evaluate(assistantMessage.Content, injectedResults);
        _options.OnCitationsEvaluated?.Invoke(grounding);

        if (!_options.AppendCitationReferences || grounding.CitedSources.Count == 0)
        {
            return turnResult;
        }

        var footer = new StringBuilder(assistantMessage.Content);
        footer.AppendLine();
        footer.AppendLine();
        footer.AppendLine("References:");
        for (var i = 0; i < grounding.CitedSources.Count; i++)
        {
            var cited = grounding.CitedSources[i];
            var sourceLabel = cited.Source ?? cited.Key;
            footer.Append("- [");
            footer.Append(cited.CitationNumber.ToString(CultureInfo.InvariantCulture));
            footer.Append("] ");
            if (!string.IsNullOrWhiteSpace(cited.Title) &&
                !string.Equals(cited.Title, sourceLabel, StringComparison.Ordinal))
            {
                footer.Append(cited.Title);
                footer.Append(" (");
                footer.Append(sourceLabel);
                footer.Append(')');
            }
            else
            {
                footer.Append(sourceLabel);
            }

            if (i < grounding.CitedSources.Count - 1)
            {
                footer.AppendLine();
            }
        }

        var updatedMessages = turnResult.Output.ToArray();
        updatedMessages[lastAssistantIndex] = assistantMessage with { Content = footer.ToString() };
        return turnResult with { Output = updatedMessages };
    }

    private AgentMessage BuildRetrievedContextMessage(List<(MemorySearchResult Result, string Content)> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Retrieved context:");

        for (var i = 0; i < results.Count; i++)
        {
            var (result, content) = results[i];
            var entry = result.Entry;
            var metadata = entry.Metadata;

            var source = metadata is not null &&
                         metadata.TryGetValue(DocumentMetadataKeys.Source, out var src) &&
                         !string.IsNullOrWhiteSpace(src)
                ? src
                : entry.Key;

            builder.Append('[');
            builder.Append((i + 1).ToString(CultureInfo.InvariantCulture));
            builder.Append("] source: ");
            builder.Append(source);

            if (metadata is not null &&
                metadata.TryGetValue(DocumentMetadataKeys.DocumentTitle, out var title) &&
                !string.IsNullOrWhiteSpace(title) &&
                !string.Equals(title, source, StringComparison.Ordinal))
            {
                builder.Append(" | title: ");
                builder.Append(title);
            }

            if (metadata is not null &&
                metadata.TryGetValue(DocumentMetadataKeys.ChunkIndex, out var chunkIndex) &&
                !string.IsNullOrWhiteSpace(chunkIndex))
            {
                builder.Append(" | chunk: ");
                builder.Append(chunkIndex);
                if (metadata.TryGetValue(DocumentMetadataKeys.ChunkCount, out var chunkCount) &&
                    !string.IsNullOrWhiteSpace(chunkCount))
                {
                    builder.Append('/');
                    builder.Append(chunkCount);
                }
            }

            builder.Append(" | score: ");
            builder.Append(result.Score.ToString("0.00##", CultureInfo.InvariantCulture));
            builder.Append(": ");
            builder.AppendLine(content);
        }

        builder.Append(_options.Instruction ?? "Use this context only when it is relevant to the current request.");
        return new AgentMessage("system", builder.ToString());
    }

    private static AgentMessage[] InjectRetrievedContext(
        IReadOnlyList<AgentMessage> messages,
        AgentMessage contextMessage)
    {
        var leadingSystemCount = 0;
        while (leadingSystemCount < messages.Count &&
               string.Equals(messages[leadingSystemCount].Role, "system", StringComparison.OrdinalIgnoreCase))
        {
            leadingSystemCount++;
        }

        return messages
            .Take(leadingSystemCount)
            .Append(contextMessage)
            .Concat(messages.Skip(leadingSystemCount))
            .ToArray();
    }
}

/// <summary>
/// Feature that injects retrieved memory context into the agent turn before model invocation.
/// </summary>
public sealed class RetrievalAugmentationFeature : IAgentFeature
{
    private readonly RetrievalAugmentationOptions _options;

    public RetrievalAugmentationFeature(RetrievalAugmentationOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public void Contribute(AgentAssembly assembly)
        => assembly.Pipeline.Use(new RetrievalAugmentationMiddleware(_options));
}
