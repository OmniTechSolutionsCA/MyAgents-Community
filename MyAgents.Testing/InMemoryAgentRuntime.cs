using MyAgents.Core;

namespace MyAgents.Testing;

/// <summary>
/// An in-memory <see cref="IAgentRuntime"/> for unit testing.
/// Creates <see cref="InMemoryAgent"/> instances keyed by agent name.
/// Each named agent has its own response queue; all created agents are tracked
/// so tests can assert on invocation counts and received messages.
/// </summary>
/// <remarks>
/// Typical usage with <see cref="Core.Workflows.WorkflowHost"/>:
///
/// <code>
/// var runtime = new InMemoryAgentRuntime()
///     .ForAgent("researcher", q => q
///         .EnqueueResponse("Paris is the capital of France."))
///     .ForAgent("writer", q => q
///         .EnqueueResponse("The capital is Paris."));
///
/// var factory = new DefaultAgentSessionFactory(runtime, serviceProvider);
/// var host = new WorkflowHost(factory);
/// host.RegisterAgent("researcher", researcherDef);
/// host.RegisterAgent("writer", writerDef);
///
/// var result = await host.RunAsync(new MyWorkflow(), initialState);
///
/// Assert.Equal(1, runtime.GetAgent("researcher").InvocationCount);
/// </code>
/// </remarks>
public sealed class InMemoryAgentRuntime : IAgentRuntime
{
    // Keyed by agent definition Name (case-insensitive)
    private readonly Dictionary<string, InMemoryAgent> _agentsByName =
        new(StringComparer.OrdinalIgnoreCase);

    // Unique agents created via CreateAgent, in first-seen order
    private readonly List<InMemoryAgent> _allCreatedAgents = new();
    private readonly HashSet<InMemoryAgent> _createdAgentSet = new();

    /// <summary>All unique agents created by this runtime, in first-seen order.</summary>
    public IReadOnlyList<InMemoryAgent> CreatedAgents => _allCreatedAgents;

    /// <summary>
    /// Configures the response queue for the agent with the given name.
    /// The <paramref name="configure"/> callback receives the pre-created
    /// <see cref="InMemoryAgent"/> so you can call <c>EnqueueResponse</c> etc.
    /// </summary>
    public InMemoryAgentRuntime ForAgent(string agentName, Action<InMemoryAgent> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(configure);

        if (!_agentsByName.TryGetValue(agentName, out var agent))
        {
            agent = new InMemoryAgent(agentName);
            _agentsByName[agentName] = agent;
        }

        configure(agent);
        return this;
    }

    /// <summary>
    /// Retrieves the <see cref="InMemoryAgent"/> registered for <paramref name="agentName"/>,
    /// or throws if it was never configured.
    /// </summary>
    public InMemoryAgent GetAgent(string agentName)
    {
        if (_agentsByName.TryGetValue(agentName, out var agent))
            return agent;

        throw new KeyNotFoundException(
            $"No InMemoryAgent was configured for agent name '{agentName}'. " +
            $"Call ForAgent(\"{agentName}\", ...) before running the workflow.");
    }

    /// <summary>
    /// Returns the <see cref="InMemoryAgent"/> for <paramref name="agentName"/>,
    /// or <c>null</c> if it was never configured.
    /// </summary>
    public InMemoryAgent? TryGetAgent(string agentName) =>
        _agentsByName.GetValueOrDefault(agentName);

    /// <inheritdoc />
    /// <remarks>
    /// If a pre-configured agent exists for <see cref="AgentDefinition.Name"/>, that agent
    /// is returned (and tracked in <see cref="CreatedAgents"/> once). Otherwise a new
    /// <see cref="InMemoryAgent"/> with an empty queue (returns empty responses) is created.
    /// </remarks>
    public IAgent CreateAgent(AgentDefinition definition, AgentAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!_agentsByName.TryGetValue(definition.Name, out var agent))
        {
            // Auto-create an agent with empty queue — will return empty assistant messages
            agent = new InMemoryAgent(definition.Name);
            _agentsByName[definition.Name] = agent;
        }

        if (_createdAgentSet.Add(agent))
            _allCreatedAgents.Add(agent);

        return agent;
    }

    /// <summary>
    /// Resets all tracked invocations on every registered agent.
    /// Useful between test cases when reusing the same runtime.
    /// </summary>
    public void ResetAll()
    {
        foreach (var agent in _agentsByName.Values)
            agent.Reset();
        _allCreatedAgents.Clear();
        _createdAgentSet.Clear();
    }
}
