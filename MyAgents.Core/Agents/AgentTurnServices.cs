using Microsoft.Extensions.DependencyInjection;

namespace MyAgents.Core;

/// <summary>
/// Services available to an agent during a turn execution.
/// </summary>
public sealed class AgentTurnServices
{
    private readonly IServiceProvider _rootProvider;

    public AgentTurnServices(IServiceProvider rootProvider)
    {
        _rootProvider = rootProvider ?? throw new ArgumentNullException(nameof(rootProvider));
    }

    /// <summary>
    /// Gets a service of type T from the DI container.
    /// </summary>
    public T? GetService<T>() where T : class
        => _rootProvider.GetService(typeof(T)) as T;

    /// <summary>
    /// Gets a required service of type T from the DI container.
    /// </summary>
    public T GetRequiredService<T>() where T : class
        => (T)_rootProvider.GetRequiredService(typeof(T));
}
