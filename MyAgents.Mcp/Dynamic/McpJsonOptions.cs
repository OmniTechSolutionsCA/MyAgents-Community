using System.Text.Json;

namespace MyAgents.Mcp;

internal static class McpJsonOptions
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    public static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };
}