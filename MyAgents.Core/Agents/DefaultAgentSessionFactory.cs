using MyAgents.Core.Memory;
using MyAgents.Core.Messaging;
using MyAgents.Core.Prompts;

namespace MyAgents.Core;

/// <summary>
/// Default implementation of IAgentSessionFactory.
/// Builds the assembly once at session creation time and passes it to the runtime,
/// ensuring system prompt and tools flow correctly without duplicate assembly.
/// </summary>
public sealed class DefaultAgentSessionFactory : IAgentSessionFactory
{
    private readonly IAgentRuntime _runtime;
    private readonly IServiceProvider _rootProvider;

    public DefaultAgentSessionFactory(IAgentRuntime runtime, IServiceProvider rootProvider)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _rootProvider = rootProvider ?? throw new ArgumentNullException(nameof(rootProvider));
    }

    public IAgentSession CreateSession(string sessionId, AgentDefinition definition)
        => CreateSessionAsync(sessionId, definition).AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Creates a new session asynchronously. Preferred over <see cref="CreateSession"/> when
    /// any feature may perform async I/O during contribution (e.g. <see cref="McpFeature"/>).
    /// </summary>
    public async ValueTask<IAgentSession> CreateSessionAsync(
        string sessionId,
        AgentDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(definition);

        // Build the assembly once — features contribute tools, pipeline, system prompts, and memory.
        // ContributeAsync is awaited so features that perform async I/O (e.g. MCP tool discovery)
        // never need to block a thread pool thread via .GetAwaiter().GetResult().
        var assembly = new AgentAssembly();
        foreach (var feature in definition.Features)
            await feature.ContributeAsync(assembly).ConfigureAwait(false);

        var toolCatalog = new ToolCatalog(assembly.Tools.Build());

        // Create the runtime agent, passing the pre-built assembly so the runtime
        // can configure instructions and tools without rebuilding them.
        var agent = _runtime.CreateAgent(definition, assembly);

        // If session-scoped services were contributed, wrap the root provider in a thin overlay
        // that satisfies those resolutions while falling back to root for everything else.
        IServiceProvider sessionProvider;
        if (assembly.Memory is not null ||
            assembly.PromptRegistry is not null ||
            assembly.Channel is not null ||
            assembly.ToolObservers.Count > 0 ||
            toolCatalog is not null ||
            assembly.SessionServices.Count > 0)
        {
            sessionProvider = new SessionOverlayServiceProvider(
                _rootProvider,
                assembly.Memory,
                assembly.PromptRegistry,
                assembly.Channel,
                toolCatalog,
                assembly.ToolObservers.AsReadOnly(),
                assembly.SessionServices);
        }
        else
        {
            sessionProvider = _rootProvider;
        }

        var composedCapabilities = MergeCapabilities(definition.Capabilities, assembly.Capabilities);

        return new AgentSession(
            sessionId,
            agent,
            assembly.Pipeline.Build(),
            sessionProvider,
            composedCapabilities,
            assembly.OwnedResources);
    }

    // Satisfy the IAgentSessionFactory interface (sync entry point delegates to async)
    // — placed here so the compiler sees it as the explicit implementation.

    // ── Inner helpers ────────────────────────────────────────────────────────
    /// <summary>Merges definition-level + feature-contributed capabilities, deduplicating by Id.</summary>
    private static IReadOnlyList<IAgentCapability> MergeCapabilities(
        IReadOnlyList<IAgentCapability>? definitionCaps,
        List<IAgentCapability> assemblyCaps)
    {
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<IAgentCapability>();

        void Add(IAgentCapability cap)
        {
            if (seen.Add(cap.Id)) merged.Add(cap);
        }

        if (definitionCaps is not null)
            foreach (var c in definitionCaps) Add(c);

        foreach (var c in assemblyCaps) Add(c);

        return merged;
    }


    /// <summary>
    /// Minimal IServiceProvider overlay that serves session-scoped services
    /// (<see cref="IMemoryStore"/>, <see cref="PromptRegistry"/>) and delegates
    /// everything else to the root provider.
    /// </summary>
    private sealed class SessionOverlayServiceProvider : IServiceProvider
    {
        private readonly IServiceProvider _root;
        private readonly IMemoryStore? _store;
        private readonly PromptRegistry? _registry;
        private readonly IAgentChannel? _channel;
        private readonly ToolCatalog? _toolCatalog;
        private readonly IReadOnlyList<Tools.IToolCallObserver>? _toolObservers;
        private readonly IReadOnlyDictionary<Type, object> _sessionServices;

        public SessionOverlayServiceProvider(
            IServiceProvider root,
            IMemoryStore? store,
            PromptRegistry? registry,
            IAgentChannel? channel,
            ToolCatalog? toolCatalog,
            IReadOnlyList<Tools.IToolCallObserver>? toolObservers,
            IReadOnlyDictionary<Type, object>? sessionServices)
        {
            _root     = root;
            _store    = store;
            _registry = registry;
            _channel  = channel;
            _toolCatalog = toolCatalog;
            _toolObservers = toolObservers;
            _sessionServices = sessionServices ?? new Dictionary<Type, object>();
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IMemoryStore)  && _store    is not null) return _store;
            if (serviceType == typeof(PromptRegistry) && _registry is not null) return _registry;
            if (serviceType == typeof(IAgentChannel)  && _channel  is not null) return _channel;
            if (serviceType == typeof(ToolCatalog)    && _toolCatalog is not null) return _toolCatalog;
            if (serviceType == typeof(IReadOnlyList<Tools.IToolCallObserver>) && _toolObservers is not null) return _toolObservers;
            if (_sessionServices.TryGetValue(serviceType, out var service)) return service;
            return _root.GetService(serviceType);
        }
    }

    private sealed class AgentSession : IAgentSession
    {
        public string SessionId { get; }
        public IReadOnlyList<IAgentCapability> Capabilities { get; }

        private readonly IAgent _agent;
        private readonly AgentPipeline _pipeline;
        private readonly IServiceProvider _rootProvider;
        private readonly IReadOnlyList<object> _ownedResources;
        private int _disposeState;

        public AgentSession(
            string sessionId,
            IAgent agent,
            AgentPipeline pipeline,
            IServiceProvider rootProvider,
            IReadOnlyList<IAgentCapability> capabilities,
            IReadOnlyList<object> ownedResources)
        {
            SessionId     = sessionId;
            _agent        = agent;
            _pipeline     = pipeline;
            _rootProvider = rootProvider;
            Capabilities  = capabilities;
            _ownedResources = ownedResources;
        }
        public ValueTask<AgentTurnResult> InvokeAsync(
            IReadOnlyList<AgentMessage> messages,
            CancellationToken cancellationToken = default)
        {
            var context = new AgentTurnContext(SessionId, messages, new AgentTurnServices(_rootProvider));
            return _pipeline.InvokeAsync(_agent, context, cancellationToken);
        }

        public IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
            IReadOnlyList<AgentMessage> messages,
            CancellationToken cancellationToken = default)
        {
            var context = new AgentTurnContext(SessionId, messages, new AgentTurnServices(_rootProvider));
            return _pipeline.InvokeStreamingAsync(_agent, context, cancellationToken);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) != 0)
                return;

            var exceptions = new List<Exception>();

            DisposeResource(_agent, exceptions);

            for (var index = _ownedResources.Count - 1; index >= 0; index--)
            {
                DisposeResource(_ownedResources[index], exceptions);
            }

            if (exceptions.Count > 0)
                throw new AggregateException(exceptions);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) != 0)
                return;

            var exceptions = new List<Exception>();

            await DisposeResourceAsync(_agent, exceptions).ConfigureAwait(false);

            for (var index = _ownedResources.Count - 1; index >= 0; index--)
            {
                await DisposeResourceAsync(_ownedResources[index], exceptions).ConfigureAwait(false);
            }

            if (exceptions.Count > 0)
                throw new AggregateException(exceptions);
        }

        private static void DisposeResource(object resource, List<Exception> exceptions)
        {
            try
            {
                switch (resource)
                {
                    case IAsyncDisposable asyncDisposable:
                        asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }

        private static async ValueTask DisposeResourceAsync(object resource, List<Exception> exceptions)
        {
            try
            {
                switch (resource)
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }
    }
}
