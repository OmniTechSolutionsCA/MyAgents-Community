using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyAgents.Core;

namespace MyAgents.Agents;

/// <summary>
/// Builder for configuring hierarchical subagent registrations.
/// </summary>
public sealed class SubagentOrchestratorBuilder
{
    private readonly List<AgentDefinition> _subagents = [];

    public IReadOnlyList<AgentDefinition> Subagents => _subagents;

    public SubagentOrchestratorOptions Options { get; } = new();

    public SubagentOrchestratorBuilder AddSubagent(AgentDefinition agentDefinition)
    {
        ArgumentNullException.ThrowIfNull(agentDefinition);
        _subagents.Add(agentDefinition);
        return this;
    }

    public SubagentOrchestratorBuilder ConfigureOptions(Action<SubagentOrchestratorOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Options);
        return this;
    }
}

/// <summary>
/// Service collection extensions for hierarchical subagent orchestration.
/// </summary>
public static class SubagentServiceExtensions
{
    /// <summary>
    /// Registers the hierarchical subagent dispatcher and configured subagents.
    /// </summary>
    public static IServiceCollection AddHierarchicalSubagents(
        this IServiceCollection services,
        Action<SubagentOrchestratorBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new SubagentOrchestratorBuilder();
        configure(builder);

        services.TryAddSingleton<ISubagentDispatcher>(sp =>
        {
            var factory = sp.GetRequiredService<IAgentSessionFactory>();
            return new HierarchicalSubagentDispatcher(factory, builder.Subagents, builder.Options);
        });

        services.TryAddTransient<SubagentDelegationFeature>(sp =>
        {
            var dispatcher = sp.GetRequiredService<ISubagentDispatcher>();
            return new SubagentDelegationFeature(dispatcher);
        });

        return services;
    }
}