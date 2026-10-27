using ModelContextProtocol.Client;
using MyAgents.Core.Tools;

namespace MyAgents.Mcp;

/// <summary>
/// Status of an active or managed MCP server connection.
/// </summary>
public enum McpServerStatus
{
    Connected,
    Disconnected,
    Reloading,
    Faulted
}

/// <summary>
/// Informational snapshot of an active MCP server and its tool registrations.
/// </summary>
public sealed record McpServerInfo(
    string Name,
    McpServerStatus Status,
    int ToolCount,
    IReadOnlyList<string> DiscoveredTools,
    DateTimeOffset LastRefreshedAt,
    string? ErrorMessage = null);

/// <summary>
/// Outcome of a hot-reload cycle for a single MCP server.
/// </summary>
public sealed record McpServerReloadResult(
    string ServerName,
    bool Success,
    int PreviousToolCount,
    int NewToolCount,
    IReadOnlyList<string> AddedTools,
    IReadOnlyList<string> RemovedTools,
    string? ErrorMessage = null);

/// <summary>
/// Manager for dynamically managing MCP server connections, hot-reloading tool catalogs,
/// and notifying listeners of runtime catalog mutations.
/// </summary>
public interface IMcpServerHotReloadManager : IAsyncDisposable
{
    /// <summary>
    /// Gets operational metadata for all registered MCP servers.
    /// </summary>
    IReadOnlyList<McpServerInfo> GetActiveServers();

    /// <summary>
    /// Reloads the tool catalog for a specific registered MCP server by name.
    /// </summary>
    ValueTask<McpServerReloadResult> ReloadServerAsync(string serverName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reloads tool catalogs across all registered MCP servers.
    /// </summary>
    ValueTask<IReadOnlyList<McpServerReloadResult>> ReloadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Dynamically attaches a new MCP server at runtime and indexes its tools.
    /// </summary>
    ValueTask<bool> RegisterServerAsync(string name, IClientTransport transport, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dynamically detaches an MCP server at runtime and removes its tools.
    /// </summary>
    ValueTask<bool> UnregisterServerAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Event raised whenever an MCP server's tools are reloaded or modified.
    /// </summary>
    event EventHandler<McpServerReloadResult>? OnServerReloaded;
}