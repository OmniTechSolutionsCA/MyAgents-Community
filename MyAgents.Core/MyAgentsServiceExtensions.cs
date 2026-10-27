using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyAgents.Core.Tools;

namespace MyAgents.Core;

/// <summary>
/// Extension methods for registering MyAgents framework services into an
/// <see cref="IServiceCollection"/>.
/// </summary>
public static class MyAgentsServiceExtensions
{
    /// <summary>
    /// Registers the core MyAgents services required to create and run agent sessions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This registers <see cref="IAgentSessionFactory"/> as a singleton backed by
    /// <see cref="DefaultAgentSessionFactory"/>. The factory depends on an
    /// <see cref="IAgentRuntime"/> that must be registered separately (either directly
    /// or via a runtime-specific extension such as <c>AddMafRuntime()</c>).
    /// </para>
    /// <para>
    /// Registration uses <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService,TImplementation}(IServiceCollection)"/>
    /// so calling this method multiple times is safe — the second call is a no-op.
    /// </para>
    /// <example>
    /// <code>
    /// services.AddMyAgents();
    /// // …then register your runtime:
    /// services.AddSingleton&lt;IAgentRuntime, MyRuntime&gt;();
    /// </code>
    /// </example>
    /// </remarks>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddMyAgents(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IAgentSessionFactory, DefaultAgentSessionFactory>();

        // default process runner used by tools that spawn external executables
        services.TryAddSingleton<IProcessRunner, DefaultProcessRunner>();
        services.TryAddSingleton<IToolApprovalPolicy, AllowAllToolApprovalPolicy>();
        services.TryAddSingleton<IToolApprovalStore, InMemoryToolApprovalStore>();
        services.TryAddSingleton<ISessionApprovalMemory, InMemorySessionApprovalMemory>();
        services.TryAddSingleton<IToolApprovalAuditSink, InMemoryToolApprovalAuditSink>();
        services.TryAddSingleton<IRunTraceSink, InMemoryRunTraceSink>();
        return services;
    }

    /// <summary>
    /// Registers the core MyAgents services and the provided <paramref name="runtime"/>
    /// as the <see cref="IAgentRuntime"/> singleton.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="runtime">The agent runtime instance to register.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddMyAgents(
        this IServiceCollection services,
        IAgentRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(runtime);

        services.TryAddSingleton(runtime);
        return services.AddMyAgents();
    }

    /// <summary>
    /// Registers the core MyAgents services, creating the <see cref="IAgentRuntime"/>
    /// via the <paramref name="runtimeFactory"/> delegate.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="runtimeFactory">
    /// A factory delegate called once (at resolve time) to create the
    /// <see cref="IAgentRuntime"/>. The <see cref="IServiceProvider"/> is supplied
    /// for resolving any transitive dependencies the runtime needs.
    /// </param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddMyAgents(
        this IServiceCollection services,
        Func<IServiceProvider, IAgentRuntime> runtimeFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(runtimeFactory);

        services.TryAddSingleton<IAgentRuntime>(runtimeFactory);
        return services.AddMyAgents();
    }
}
