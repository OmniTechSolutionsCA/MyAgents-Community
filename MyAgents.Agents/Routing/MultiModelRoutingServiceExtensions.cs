using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MyAgents.Agents;

/// <summary>
/// Builder for configuring multi-model routing candidates and policies.
/// </summary>
public sealed class MultiModelRoutingBuilder
{
    private readonly List<ModelCandidate> _candidates = [];

    public IReadOnlyList<ModelCandidate> Candidates => _candidates;

    public MultiModelRoutingOptions Options { get; } = new();

    public MultiModelRoutingBuilder AddCandidate(ModelCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        _candidates.Add(candidate);
        return this;
    }

    public MultiModelRoutingBuilder AddCandidate(
        string modelId,
        IChatClient client,
        ModelTier tier = ModelTier.Standard,
        ModelPricing? pricing = null,
        int? maxInputTokens = null,
        bool isFallback = false)
    {
        return AddCandidate(new ModelCandidate(modelId, client, tier, pricing, maxInputTokens, isFallback));
    }

    public MultiModelRoutingBuilder ConfigureOptions(Action<MultiModelRoutingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Options);
        return this;
    }
}

/// <summary>
/// Service collection extensions for registering multi-model routing and token/cost optimization.
/// </summary>
public static class MultiModelRoutingServiceExtensions
{
    /// <summary>
    /// Registers an adaptive multi-model routing <see cref="IChatClient"/> with token and cost tracking.
    /// </summary>
    public static IServiceCollection AddMultiModelRouting(
        this IServiceCollection services,
        Action<MultiModelRoutingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new MultiModelRoutingBuilder();
        configure(builder);

        services.TryAddSingleton<ITokenCostTelemetrySink, InMemoryTokenCostTelemetrySink>();
        services.AddSingleton<MultiModelRoutingChatClient>(sp =>
        {
            var sink = sp.GetService<ITokenCostTelemetrySink>();
            return new MultiModelRoutingChatClient(builder.Candidates, builder.Options, sink);
        });
        services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<MultiModelRoutingChatClient>());

        return services;
    }
}