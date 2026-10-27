using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyAgents.Core;

namespace MyAgents.Runtime.Chat;

/// <summary>Registers the provider-neutral chat runtime for MyAgents.</summary>
public static class ChatClientRuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the MyAgents chat runtime. Register an <c>IChatClient</c> provider before
    /// resolving an agent session, then call <see cref="AddMyAgentsChatRuntime"/>.
    /// </summary>
    public static IServiceCollection AddMyAgentsChatRuntime(
        this IServiceCollection services,
        Action<ChatClientRuntimeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<ChatClientRuntimeOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton<IAgentRuntime, ChatClientRuntime>();
        return services.AddMyAgents();
    }
}
