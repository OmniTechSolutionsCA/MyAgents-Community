using MyAgents.Core.Tools;

namespace MyAgents.Mcp;

/// <summary>
/// Thread-safe, hot-reloading tool catalog that unifies base static tools with
/// dynamically attached MCP server and skill tools.
/// </summary>
public sealed class DynamicToolCatalog
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, ITool> _staticTools = new(StringComparer.OrdinalIgnoreCase);
    // sourcePrefix -> (toolName -> ITool)
    private readonly Dictionary<string, Dictionary<string, ITool>> _dynamicToolGroups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Event raised when tools are added, updated, or removed from the catalog.
    /// </summary>
    public event EventHandler? CatalogChanged;

    public DynamicToolCatalog(IEnumerable<ITool>? initialTools = null)
    {
        if (initialTools is not null)
        {
            foreach (var tool in initialTools)
            {
                if (tool is not null)
                {
                    _staticTools[tool.Name] = tool;
                }
            }
        }
    }

    /// <summary>
    /// Returns an immutable snapshot of all active tools (static and dynamic).
    /// </summary>
    public IReadOnlyList<ITool> Tools
    {
        get
        {
            lock (_sync)
            {
                var list = new List<ITool>(_staticTools.Values);
                foreach (var group in _dynamicToolGroups.Values)
                {
                    list.AddRange(group.Values);
                }
                return list.AsReadOnly();
            }
        }
    }

    /// <summary>
    /// Attempts to locate a tool by its registered name (case-insensitive).
    /// </summary>
    public bool TryGet(string name, out ITool tool)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            tool = default!;
            return false;
        }

        lock (_sync)
        {
            if (_staticTools.TryGetValue(name, out tool!))
            {
                return true;
            }

            foreach (var group in _dynamicToolGroups.Values)
            {
                if (group.TryGetValue(name, out tool!))
                {
                    return true;
                }
            }
        }

        tool = default!;
        return false;
    }

    /// <summary>
    /// Updates or replaces the dynamic tools associated with a specific source prefix (e.g. an MCP server).
    /// </summary>
    public void SetDynamicTools(string sourcePrefix, IEnumerable<ITool> tools)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePrefix);
        ArgumentNullException.ThrowIfNull(tools);

        var toolDict = new Dictionary<string, ITool>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tools)
        {
            if (t is not null)
            {
                toolDict[t.Name] = t;
            }
        }

        lock (_sync)
        {
            _dynamicToolGroups[sourcePrefix] = toolDict;
        }

        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Removes all dynamic tools associated with a specific source prefix.
    /// </summary>
    public bool RemoveDynamicTools(string sourcePrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePrefix);

        bool removed;
        lock (_sync)
        {
            removed = _dynamicToolGroups.Remove(sourcePrefix);
        }

        if (removed)
        {
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>
    /// Registers an additional base static tool.
    /// </summary>
    public void RegisterStaticTool(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        lock (_sync)
        {
            _staticTools[tool.Name] = tool;
        }

        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }
}