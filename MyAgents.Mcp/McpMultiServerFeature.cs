using ModelContextProtocol.Client;
using MyAgents.Core;
using MyAgents.Core.Prompts;

namespace MyAgents.Mcp;

/// <summary>
/// Additive multi-server MCP feature options. Defaults preserve the original tool-only behavior.
/// </summary>
public sealed record McpMultiServerFeatureOptions(
    bool ImportPrompts = false,
    bool ExposeResourceTools = false,
    IReadOnlySet<string>? AllowedToolNames = null);

/// <summary>
/// Agent feature that connects to multiple MCP servers and contributes all their tools
/// to the agent assembly. Supports both stdio and HTTP transports.
/// </summary>
/// <remarks>
/// Unlike <see cref="McpFeature"/>, this supports multiple servers and optional
/// dynamic tool refresh to pick up new tools without restarting the agent.
/// </remarks>
public sealed class McpMultiServerFeature : IAgentFeature
{
    private readonly List<ServerConfig> _servers;
    private readonly TimeSpan? _refreshInterval;
    private readonly McpMultiServerFeatureOptions _options;

    /// <summary>
    /// Configuration for a single MCP server.
    /// </summary>
    public sealed record ServerConfig(
        string Name,
        IClientTransport Transport,
        bool EnableDynamicRefresh = false);

    /// <param name="servers">List of MCP server configurations.</param>
    /// <param name="refreshInterval">
    /// Optional interval for dynamic refresh. If set, tools are re-fetched from all servers
    /// with EnableDynamicRefresh=true at this interval.
    /// </param>
    public McpMultiServerFeature(IEnumerable<ServerConfig> servers, TimeSpan? refreshInterval = null)
        : this(servers, options: null, refreshInterval)
    {
    }

    /// <summary>
    /// Creates a multi-server MCP feature with additive prompt/resource options.
    /// </summary>
    public McpMultiServerFeature(
        IEnumerable<ServerConfig> servers,
        McpMultiServerFeatureOptions? options,
        TimeSpan? refreshInterval = null)
    {
        _servers = (servers ?? throw new ArgumentNullException(nameof(servers))).ToList();
        _refreshInterval = refreshInterval;
        _options = options ?? new McpMultiServerFeatureOptions();

        if (_servers.Count == 0)
            throw new ArgumentException("At least one server must be provided.", nameof(servers));
    }

    /// <summary>
    /// Synchronous contribution entry point. Prefer the async session factory path;
    /// this overload blocks a thread-pool thread while connecting to each MCP server.
    /// </summary>
    public void Contribute(AgentAssembly assembly)
        => ContributeAsync(assembly).AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Asynchronously connects to all configured MCP servers and contributes their tools.
    /// </summary>
    public async ValueTask ContributeAsync(
        AgentAssembly assembly,
        CancellationToken cancellationToken = default)
    {
        var sessionResources = new SessionResources();
        var registrations = new List<McpServerRegistration>(_servers.Count);

        foreach (var server in _servers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var client = await McpClient.CreateAsync(
                server.Transport,
                clientOptions: null,
                loggerFactory: null,
                cancellationToken: cancellationToken);

            sessionResources.Clients.Add((client, server));

            var listedTools = await client.ListToolsAsync(
                options: null,
                cancellationToken: cancellationToken);
            var tools = _options.AllowedToolNames is { Count: > 0 } allowedToolNames
                ? listedTools.Where(tool => allowedToolNames.Contains(tool.Name)).ToArray()
                : listedTools;
            var descriptors = tools
                .Select(tool => new McpToolDescriptor($"{server.Name}_{tool.Name}", tool.Description))
                .ToArray();

            registrations.Add(new McpServerRegistration(
                Prefix: server.Name,
                DisplayName: server.Name,
                Client: client,
                Tools: descriptors));

            foreach (McpClientTool mcpTool in tools)
            {
                // Prefix tool names with server name to avoid collisions
                assembly.Tools.Add(new McpToolAdapter(client, mcpTool, prefix: server.Name));
            }
        }

        var sessionContext = new McpSessionContext(registrations);
        assembly.RegisterSessionService<IMcpSessionContext>(sessionContext);

        if (_options.ImportPrompts)
        {
            assembly.PromptRegistry ??= new PromptRegistry();
            await sessionContext.ImportPromptsAsync(assembly.PromptRegistry, cancellationToken).ConfigureAwait(false);
        }

        if (_options.ExposeResourceTools)
        {
            foreach (var server in _servers)
            {
                assembly.Tools.Add(new McpListResourcesTool(sessionContext, prefix: server.Name, serverPrefix: server.Name));
                assembly.Tools.Add(new McpReadResourceTool(sessionContext, prefix: server.Name, serverPrefix: server.Name));
            }
        }

        if (_refreshInterval.HasValue && _refreshInterval.Value > TimeSpan.Zero)
        {
            sessionResources.RefreshTimer = new Timer(
                static state =>
                {
                    var (resources, feature) = ((SessionResources, McpMultiServerFeature))state!;
                    _ = feature.RefreshToolsAsync(resources);
                },
                (sessionResources, this),
                _refreshInterval.Value,
                _refreshInterval.Value);
        }

        assembly.RegisterOwnedResource(sessionResources);
    }

    /// <summary>
    /// Event raised when the tool list changes on a server with dynamic refresh enabled.
    /// </summary>
    public event EventHandler<ToolListChangedEventArgs>? ToolListChanged;

    private async Task RefreshToolsAsync(SessionResources resources)
    {
        foreach (var (client, config) in resources.Clients)
        {
            if (!config.EnableDynamicRefresh) continue;

            try
            {
                var tools = await client.ListToolsAsync(
                    options: null,
                    cancellationToken: CancellationToken.None);

                ToolListChanged?.Invoke(this, new ToolListChangedEventArgs(config.Name, tools));
            }
            catch
            {
                // Silently ignore refresh failures - don't crash the agent
            }
        }
    }

    private sealed class SessionResources : IAsyncDisposable
    {
        public List<(McpClient Client, ServerConfig Config)> Clients { get; } = new();

        public Timer? RefreshTimer { get; set; }

        public async ValueTask DisposeAsync()
        {
            RefreshTimer?.Dispose();

            foreach (var (client, _) in Clients)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            Clients.Clear();
        }
    }
}

/// <summary>
/// Event args for tool list changes.
/// </summary>
public sealed class ToolListChangedEventArgs : EventArgs
{
    public string ServerName { get; }
    public IList<McpClientTool> Tools { get; }

    public ToolListChangedEventArgs(string serverName, IList<McpClientTool> tools)
    {
        ServerName = serverName;
        Tools = tools;
    }
}
