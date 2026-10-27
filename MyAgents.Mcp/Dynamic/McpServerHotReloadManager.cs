using System.Collections.Concurrent;
using ModelContextProtocol.Client;
using MyAgents.Core.Tools;

namespace MyAgents.Mcp;

/// <summary>
/// Runtime manager for MCP server connections, dynamic tool discovery, and zero-downtime hot-reloading.
/// </summary>
public sealed class McpServerHotReloadManager : IMcpServerHotReloadManager
{
    private sealed class ManagedServer
    {
        public required string Name { get; init; }
        public required IClientTransport Transport { get; init; }
        public required McpClient Client { get; set; }
        public McpServerStatus Status { get; set; } = McpServerStatus.Connected;
        public IReadOnlyList<string> DiscoveredTools { get; set; } = Array.Empty<string>();
        public DateTimeOffset LastRefreshedAt { get; set; } = DateTimeOffset.UtcNow;
        public string? ErrorMessage { get; set; }
    }

    private readonly DynamicToolCatalog _catalog;
    private readonly ConcurrentDictionary<string, ManagedServer> _servers = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<McpServerReloadResult>? OnServerReloaded;

    public McpServerHotReloadManager(DynamicToolCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <inheritdoc />
    public IReadOnlyList<McpServerInfo> GetActiveServers()
    {
        return _servers.Values.Select(s => new McpServerInfo(
            Name: s.Name,
            Status: s.Status,
            ToolCount: s.DiscoveredTools.Count,
            DiscoveredTools: s.DiscoveredTools,
            LastRefreshedAt: s.LastRefreshedAt,
            ErrorMessage: s.ErrorMessage)).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<bool> RegisterServerAsync(
        string name,
        IClientTransport transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(transport);

        if (_servers.ContainsKey(name))
        {
            return false;
        }

        try
        {
            var client = await McpClient.CreateAsync(
                transport,
                clientOptions: null,
                loggerFactory: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var server = new ManagedServer
            {
                Name = name,
                Transport = transport,
                Client = client,
                Status = McpServerStatus.Connected,
                LastRefreshedAt = DateTimeOffset.UtcNow
            };

            _servers[name] = server;

            await ReloadServerInternalAsync(server, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _servers[name] = new ManagedServer
            {
                Name = name,
                Transport = transport,
                Client = null!,
                Status = McpServerStatus.Faulted,
                ErrorMessage = ex.Message,
                LastRefreshedAt = DateTimeOffset.UtcNow
            };
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> UnregisterServerAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!_servers.TryRemove(name, out var server))
        {
            return false;
        }

        server.Status = McpServerStatus.Disconnected;
        _catalog.RemoveDynamicTools(name);

        if (server.Client is not null)
        {
            await server.Client.DisposeAsync().ConfigureAwait(false);
        }

        var result = new McpServerReloadResult(
            ServerName: name,
            Success: true,
            PreviousToolCount: server.DiscoveredTools.Count,
            NewToolCount: 0,
            AddedTools: Array.Empty<string>(),
            RemovedTools: server.DiscoveredTools);

        OnServerReloaded?.Invoke(this, result);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<McpServerReloadResult> ReloadServerAsync(
        string serverName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);

        if (!_servers.TryGetValue(serverName, out var server))
        {
            return new McpServerReloadResult(
                ServerName: serverName,
                Success: false,
                PreviousToolCount: 0,
                NewToolCount: 0,
                AddedTools: Array.Empty<string>(),
                RemovedTools: Array.Empty<string>(),
                ErrorMessage: $"MCP server '{serverName}' is not registered.");
        }

        return await ReloadServerInternalAsync(server, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<McpServerReloadResult>> ReloadAllAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<McpServerReloadResult>(_servers.Count);
        foreach (var server in _servers.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var res = await ReloadServerInternalAsync(server, cancellationToken).ConfigureAwait(false);
            list.Add(res);
        }
        return list;
    }

    private async ValueTask<McpServerReloadResult> ReloadServerInternalAsync(
        ManagedServer server,
        CancellationToken cancellationToken)
    {
        var prevTools = server.DiscoveredTools;
        server.Status = McpServerStatus.Reloading;

        try
        {
            var mcpTools = await server.Client.ListToolsAsync(
                options: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var adaptedTools = new List<ITool>();
            var newToolNames = new List<string>();

            foreach (McpClientTool t in mcpTools)
            {
                var adapted = new McpToolAdapter(server.Client, t, prefix: server.Name);
                adaptedTools.Add(adapted);
                newToolNames.Add(adapted.Name);
            }

            _catalog.SetDynamicTools(server.Name, adaptedTools);

            var added = newToolNames.Except(prevTools, StringComparer.OrdinalIgnoreCase).ToArray();
            var removed = prevTools.Except(newToolNames, StringComparer.OrdinalIgnoreCase).ToArray();

            server.DiscoveredTools = newToolNames;
            server.Status = McpServerStatus.Connected;
            server.LastRefreshedAt = DateTimeOffset.UtcNow;
            server.ErrorMessage = null;

            var reloadResult = new McpServerReloadResult(
                ServerName: server.Name,
                Success: true,
                PreviousToolCount: prevTools.Count,
                NewToolCount: newToolNames.Count,
                AddedTools: added,
                RemovedTools: removed);

            OnServerReloaded?.Invoke(this, reloadResult);
            return reloadResult;
        }
        catch (Exception ex)
        {
            server.Status = McpServerStatus.Faulted;
            server.ErrorMessage = ex.Message;

            var errorResult = new McpServerReloadResult(
                ServerName: server.Name,
                Success: false,
                PreviousToolCount: prevTools.Count,
                NewToolCount: prevTools.Count,
                AddedTools: Array.Empty<string>(),
                RemovedTools: Array.Empty<string>(),
                ErrorMessage: ex.Message);

            OnServerReloaded?.Invoke(this, errorResult);
            return errorResult;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers.Values)
        {
            if (server.Client is not null)
            {
                await server.Client.DisposeAsync().ConfigureAwait(false);
            }
        }
        _servers.Clear();
    }
}