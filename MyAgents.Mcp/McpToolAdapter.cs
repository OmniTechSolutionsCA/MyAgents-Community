using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using MyAgents.Core.Tools;

namespace MyAgents.Mcp;

/// <summary>
/// Adapts a single <see cref="McpClientTool"/> as an <see cref="ITool"/>.
/// Preserves the MCP tool name and description so the LLM runtime gets
/// accurate metadata for function-calling decisions.
/// </summary>
internal sealed class McpToolAdapter : ITool
{
    private readonly McpClient _client;
    private readonly McpClientTool _mcpTool;
    private readonly string _prefix;

    public string Name => string.IsNullOrEmpty(_prefix) ? _mcpTool.Name : $"{_prefix}_{_mcpTool.Name}";
    public string? Description => _mcpTool.Description;
    public string? ParametersSchemaJson => _mcpTool.JsonSchema.GetRawText();

    public McpToolAdapter(McpClient client, McpClientTool mcpTool, string? prefix = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _mcpTool = mcpTool ?? throw new ArgumentNullException(nameof(mcpTool));
        _prefix = prefix ?? string.Empty;
    }

    public async ValueTask<string> InvokeAsync(
        string jsonArguments,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, object?>? args = null;
        if (!string.IsNullOrWhiteSpace(jsonArguments))
        {
            args = JsonSerializer.Deserialize<Dictionary<string, object?>>(jsonArguments);
        }

        CallToolResult result = await _client.CallToolAsync(
            _mcpTool.Name,
            args,
            progress: null,
            options: null,
            cancellationToken);

        if (result.IsError == true)
        {
            return JsonSerializer.Serialize(new { error = true, content = result.Content });
        }

        // Extract text from TextContentBlock instances; fall back to full serialization.
        List<string> textParts = result.Content
            .OfType<TextContentBlock>()
            .Select(b => b.Text)
            .ToList();

        return textParts.Count > 0
            ? string.Join("\n", textParts)
            : JsonSerializer.Serialize(result.Content);
    }
}
