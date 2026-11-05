using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyAgents.Core;
using MyAgents.Core.Execution;

namespace MyAgents.Runtime.Workers;

/// <summary>Dependency-injection registration for the optional hosted run worker.</summary>
public static class AgentRunWorkerServiceCollectionExtensions
{
    /// <summary>
    /// Registers a background worker that consumes an application-provided
    /// <see cref="MyAgents.Core.Execution.IAgentRunBackend"/> and registered run handlers.
    /// </summary>
    /// <remarks>
    /// Register the backend and one or more <see cref="MyAgents.Core.Execution.IAgentRunHandler"/>
    /// implementations before calling <c>Build</c>. Multiple application replicas may run this
    /// worker when the selected backend provides atomic distributed claims.
    /// </remarks>
    public static IServiceCollection AddMyAgentsRunWorker(
        this IServiceCollection services,
        Action<AgentRunWorkerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<AgentRunWorkerOptions>();
        if (configure is not null)
            options.Configure(configure);

        services.AddSingleton<IAgentRunBatchCatalog>(serviceProvider =>
            serviceProvider.GetRequiredService<IAgentRunBackend>() as IAgentRunBatchCatalog
            ?? throw new InvalidOperationException("The configured run backend does not support batch queries."));
        services.AddSingleton<AgentRunBatchSubmitter>();
        services.AddSingleton<AgentRunBatchReporter>(serviceProvider => new AgentRunBatchReporter(
            serviceProvider.GetRequiredService<IAgentRunBatchCatalog>(),
            serviceProvider.GetService<IRunTraceSink>()));
        services.AddSingleton<AgentRunBatchController>(serviceProvider =>
            new AgentRunBatchController(serviceProvider.GetRequiredService<IAgentRunBackend>() as IAgentRunBatchController
                ?? throw new InvalidOperationException("The configured run backend does not support batch cancellation.")));
        services.AddHostedService<AgentRunWorkerService>();
        return services;
    }
}
