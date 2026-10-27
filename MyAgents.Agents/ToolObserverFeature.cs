using MyAgents.Core;
using MyAgents.Core.Tools;

namespace MyAgents.Agents;

/// <summary>
/// Feature that registers one or more <see cref="IToolCallObserver"/> instances
/// into the agent assembly so the adapter fires them around every tool invocation.
/// </summary>
/// <example>
/// <code>
/// var agent = new AgentDefinition(
///     Name: "TracedAgent",
///     Features: new IAgentFeature[]
///     {
///         new WebSearchFeature(provider),
///         new ToolObserverFeature(new LoggingToolCallObserver(logger)),
///     });
/// </code>
/// </example>
public sealed class ToolObserverFeature : IAgentFeature
{
    private readonly IToolCallObserver[] _observers;

    public ToolObserverFeature(params IToolCallObserver[] observers)
    {
        ArgumentNullException.ThrowIfNull(observers);
        _observers = observers;
    }

    public void Contribute(AgentAssembly assembly)
    {
        foreach (var obs in _observers)
            assembly.ToolObservers.Add(obs);
    }
}
