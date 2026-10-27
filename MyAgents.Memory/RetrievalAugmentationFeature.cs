using System.Text;
using MyAgents.Core;

namespace MyAgents.Memory;

/// <summary>
/// Options for retrieval augmentation.
/// </summary>
public sealed record RetrievalAugmentationOptions(
    string Namespace,
    int MaxResults = 5,
    double MinScore = 0.0,
    IReadOnlyDictionary<string, string>? MetadataFilters = null,
    string? Instruction = null);

internal sealed class RetrievalAugmentationMiddleware : IAgentMiddleware
{
    private readonly RetrievalAugmentationOptions _options;

    public RetrievalAugmentationMiddleware(RetrievalAugmentationOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.Namespace);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxResults, 1);
    }

    public async ValueTask<AgentTurnResult> InvokeAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>> next,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var augmentedContext = await AugmentContextAsync(context, cancellationToken).ConfigureAwait(false);
        return await next(augmentedContext, cancellationToken).ConfigureAwait(false);
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
        var augmentedContext = await AugmentContextAsync(context, cancellationToken).ConfigureAwait(false);
        await foreach (var chunk in next(augmentedContext, cancellationToken).ConfigureAwait(false))
            yield return chunk;
    }

    private async ValueTask<AgentTurnContext> AugmentContextAsync(
        AgentTurnContext context,
        CancellationToken cancellationToken)
    {
        var retriever = context.Services.GetService<IMemoryRetriever>();
        var queryText = context.Messages
            .LastOrDefault(static message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(message.Content))
            ?.Content;

        if (retriever is null || string.IsNullOrWhiteSpace(queryText))
            return context;

        var results = await retriever.SearchAsync(
            _options.Namespace,
            new MemoryQuery(
                Text: queryText,
                MaxResults: _options.MaxResults,
                MinScore: _options.MinScore,
                MetadataFilters: _options.MetadataFilters),
            cancellationToken).ConfigureAwait(false);

        if (results.Count == 0)
            return context;

        var augmentedMessages = InjectRetrievedContext(context.Messages, BuildRetrievedContextMessage(results));
        return context with { Messages = augmentedMessages };
    }

    private AgentMessage BuildRetrievedContextMessage(IReadOnlyList<MemorySearchResult> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Retrieved context:");

        foreach (var result in results)
        {
            builder.Append("- ");
            builder.Append(result.Entry.Key);
            builder.Append(": ");
            builder.AppendLine(result.Entry.Content);
        }

        builder.Append(_options.Instruction ?? "Use this context only when it is relevant to the current request.");
        return new AgentMessage("system", builder.ToString());
    }

    private static IReadOnlyList<AgentMessage> InjectRetrievedContext(
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
