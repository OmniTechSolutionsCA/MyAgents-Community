using System.Text.Json;
using MyAgents.Core;
using MyAgents.Core.Tools;

namespace MyAgents.Mcp;

/// <summary>
/// Tool that lists active MCP servers, connection health, and registered tool counts.
/// </summary>
public sealed class ListActiveMcpServersTool : ITool
{
    private readonly IMcpServerHotReloadManager _manager;

    public string Name => "list_active_mcp_servers";
    public string? Description => "Lists all active MCP server connections, their health status, and available tools.";
    public string? ParametersSchemaJson => """{"type":"object","properties":{}}""";

    public ListActiveMcpServersTool(IMcpServerHotReloadManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    public ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        var servers = _manager.GetActiveServers();
        var json = JsonSerializer.Serialize(servers, new JsonSerializerOptions { WriteIndented = true });
        return ValueTask.FromResult(json);
    }
}

/// <summary>
/// Tool that triggers hot-reloading of tools on specific or all active MCP servers.
/// </summary>
public sealed class ReloadMcpServerTool : ITool
{
    private sealed record ReloadArgs(string? server_name);

    private readonly IMcpServerHotReloadManager _manager;

    public string Name => "reload_mcp_servers";
    public string? Description => "Hot-reloads tools on a specific MCP server or across all connected MCP servers.";
    public string? ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "server_name": {
              "type": "string",
              "description": "Optional name of the specific MCP server to reload. If omitted, reloads all servers."
            }
          }
        }
        """;

    public ReloadMcpServerTool(IMcpServerHotReloadManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    public async ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        string? serverName = null;
        if (!string.IsNullOrWhiteSpace(jsonArguments))
        {
            try
            {
                var args = JsonSerializer.Deserialize<ReloadArgs>(jsonArguments, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                serverName = args?.server_name;
            }
            catch
            {
                // Fall back to no server name if json parsing fails
            }
        }

        if (!string.IsNullOrWhiteSpace(serverName))
        {
            var res = await _manager.ReloadServerAsync(serverName, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(res, new JsonSerializerOptions { WriteIndented = true });
        }
        else
        {
            var allRes = await _manager.ReloadAllAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(allRes, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}

/// <summary>
/// Tool that scans a directory dynamically to discover skill packs and tool definitions.
/// </summary>
public sealed class DiscoverSkillsTool : ITool
{
    private sealed record DiscoverArgs(string? directory_path);

    private readonly ISkillDiscoveryService _discoveryService;

    public string Name => "discover_skills";
    public string? Description => "Discovers skill manifests and capability packs from a local storage directory.";
    public string? ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "directory_path": {
              "type": "string",
              "description": "The directory path to search for skill manifest files."
            }
          },
          "required": ["directory_path"]
        }
        """;

    public DiscoverSkillsTool(ISkillDiscoveryService discoveryService)
    {
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
    }

    public async ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        string? dir = null;
        if (!string.IsNullOrWhiteSpace(jsonArguments))
        {
            try
            {
                var args = JsonSerializer.Deserialize<DiscoverArgs>(jsonArguments, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                dir = args?.directory_path;
            }
            catch
            {
                // Ignored
            }
        }

        if (string.IsNullOrWhiteSpace(dir))
        {
            return JsonSerializer.Serialize(new { error = "Argument 'directory_path' is required." });
        }

        var skills = await _discoveryService.DiscoverSkillsAsync(dir, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(skills, new JsonSerializerOptions { WriteIndented = true });
    }
}

/// <summary>
/// Feature that registers MCP hot-reload management and dynamic skill discovery into the agent assembly.
/// </summary>
public sealed class McpHotReloadFeature : IAgentFeature
{
    private readonly IMcpServerHotReloadManager _manager;
    private readonly DynamicToolCatalog _catalog;
    private readonly ISkillDiscoveryService _discoveryService;

    public McpHotReloadFeature(
        IMcpServerHotReloadManager manager,
        DynamicToolCatalog catalog,
        ISkillDiscoveryService? discoveryService = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _discoveryService = discoveryService ?? new McpSkillDiscoveryService();
    }

    public void Contribute(AgentAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        assembly.RegisterSessionService<IMcpServerHotReloadManager>(_manager);
        assembly.RegisterSessionService<DynamicToolCatalog>(_catalog);
        assembly.RegisterSessionService<ISkillDiscoveryService>(_discoveryService);

        assembly.Tools.Add(new ListActiveMcpServersTool(_manager));
        assembly.Tools.Add(new ReloadMcpServerTool(_manager));
        assembly.Tools.Add(new DiscoverSkillsTool(_discoveryService));
    }

    public ValueTask ContributeAsync(AgentAssembly assembly, CancellationToken cancellationToken = default)
    {
        Contribute(assembly);
        return ValueTask.CompletedTask;
    }
}