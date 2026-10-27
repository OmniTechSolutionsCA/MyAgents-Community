using MyAgents.Core.Tools;

namespace MyAgents.Core;

/// <summary>
/// Immutable lookup over the tools contributed during agent assembly.
/// Exposed as a session-scoped service so middleware and custom runtimes can
/// resolve tools by name without depending on a specific adapter.
/// </summary>
public sealed class ToolCatalog
{
    private readonly IReadOnlyDictionary<string, ITool> _toolsByName;

    public ToolCatalog(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var list = tools.ToArray();
        var map = new Dictionary<string, ITool>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in list)
        {
            ArgumentNullException.ThrowIfNull(tool);

            if (!map.TryAdd(tool.Name, tool))
            {
                throw new InvalidOperationException(
                    $"A tool named '{tool.Name}' was registered more than once. Tool names must be unique.");
            }
        }

        Tools = Array.AsReadOnly(list);
        _toolsByName = map;
    }

    /// <summary>
    /// All tools in registration order.
    /// </summary>
    public IReadOnlyList<ITool> Tools { get; }

    /// <summary>
    /// Attempts to resolve a tool by name.
    /// </summary>
    public bool TryGet(string name, out ITool tool)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            tool = default!;
            return false;
        }

        return _toolsByName.TryGetValue(name, out tool!);
    }
}