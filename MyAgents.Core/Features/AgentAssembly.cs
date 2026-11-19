using MyAgents.Core.Messaging;
using MyAgents.Core.Memory;
using MyAgents.Core.Prompts;
using MyAgents.Core.Tools;

namespace MyAgents.Core;

/// <summary>
/// Assembly context for building an agent. Features contribute to this assembly during the session creation process.
/// </summary>
public sealed class AgentAssembly
{
    /// <summary>
    /// The id of the session this assembly is being built for, assigned before features contribute.
    /// <see langword="null"/> only for an assembly constructed outside a session factory (for example
    /// in a test or a tool that inspects features directly).
    /// </summary>
    public string? SessionId { get; internal set; }

    private readonly List<object> _ownedResources = new();
    private readonly Dictionary<Type, object> _sessionServices = new();

    /// <summary>
    /// Builder for registering tools.
    /// </summary>
    public ToolCatalogBuilder Tools { get; } = new();

    /// <summary>
    /// Builder for registering middleware.
    /// </summary>
    public PipelineBuilder Pipeline { get; } = new();

    /// <summary>
    /// Builder for registering system prompts.
    /// </summary>
    public PromptBuilder Prompts { get; } = new();

    private IMemoryStore? _memory;
    private Func<IMemoryStore, IMemoryStore>? _memoryDecorator;

    /// <summary>
    /// Optional memory store contributed by <c>MemoryFeature</c>.
    /// When set, the store is made available as <see cref="IMemoryStore"/> via
    /// <see cref="AgentTurnServices"/> during every turn.
    /// </summary>
    public IMemoryStore? Memory
    {
        get => _memory;
        set => _memory = value is not null && _memoryDecorator is not null
            ? _memoryDecorator(value)
            : value;
    }

    internal void DecorateMemory(Func<IMemoryStore, IMemoryStore> decorator)
    {
        ArgumentNullException.ThrowIfNull(decorator);

        var previous = _memoryDecorator;
        _memoryDecorator = previous is null
            ? decorator
            : store => decorator(previous(store));

        if (_memory is not null)
        {
            _memory = decorator(_memory);
        }
    }

    /// <summary>
    /// Optional prompt registry contributed by <c>PromptRegistryFeature</c>.
    /// When set, the registry is made available as <see cref="PromptRegistry"/> via
    /// <see cref="AgentTurnServices"/> during every turn.
    /// </summary>
    public PromptRegistry? PromptRegistry { get; set; }
    /// <summary>
    /// Mutable list of capabilities declared by features during assembly.
    /// <c>CapabilityFeature</c> (or any feature) may add entries here.
    /// The final list is used to populate <see cref="AgentDefinition.Capabilities"/> at
    /// query time via <see cref="AgentDefinition.HasCapability(string)"/>.
    /// </summary>
    public List<IAgentCapability> Capabilities { get; } = new();

    /// <summary>
    /// Optional agent channel contributed by <c>AgentChannelFeature</c>.
    /// When set, the channel is made available as <see cref="IAgentChannel"/> via
    /// <see cref="AgentTurnServices"/> during every turn.
    /// </summary>
    public IAgentChannel? Channel { get; set; }

    /// <summary>
    /// Mutable list of tool call observers registered by features.
    /// The <c>MafAgentAdapter</c> fires these before and after each tool invocation.
    /// Add entries from a feature's <see cref="IAgentFeature.Contribute(AgentAssembly)"/> method.
    /// </summary>
    public List<IToolCallObserver> ToolObservers { get; } = new();

    /// <summary>
    /// Session-scoped services contributed by features.
    /// These are exposed through <see cref="AgentTurnServices"/> for the lifetime of the session.
    /// </summary>
    public IReadOnlyDictionary<Type, object> SessionServices => _sessionServices;

    internal IReadOnlyList<object> OwnedResources => _ownedResources;

    internal void RegisterOwnedResource(object resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _ownedResources.Add(resource);
    }

    /// <summary>
    /// Registers a session-scoped service contributed during assembly.
    /// Last registration for a given service type wins.
    /// </summary>
    public void RegisterSessionService<TService>(TService service) where TService : class
    {
        ArgumentNullException.ThrowIfNull(service);
        _sessionServices[typeof(TService)] = service;
    }
}
