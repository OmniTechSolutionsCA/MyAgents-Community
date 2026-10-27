using System.Text.Json;

namespace MyAgents.Core.Tools;

/// <summary>
/// Canonical framework-level representation of a model-emitted function call.
///
/// The normalized JSON envelope emitted by <see cref="AgentFunctionCallSerializer"/>
/// is:
/// <code>
/// {
///   "type": "function_call",
///   "id": "call-1",
///   "name": "tool_name",
///   "arguments": { ... }
/// }
/// </code>
/// </summary>
/// <param name="Name">Registered tool name to invoke.</param>
/// <param name="ArgumentsJson">Raw JSON for the tool arguments payload.</param>
/// <param name="CallId">Optional model-assigned call identifier.</param>
public sealed record AgentFunctionCall(
    string Name,
    string ArgumentsJson,
    string? CallId = null)
{
    /// <summary>
    /// Creates a canonical function call by serializing a typed arguments object.
    /// </summary>
    public static AgentFunctionCall Create<TArguments>(
        string name,
        TArguments arguments,
        string? callId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new AgentFunctionCall(name, JsonSerializer.Serialize(arguments), callId);
    }

    /// <summary>
    /// Creates a canonical function call from raw JSON arguments.
    /// </summary>
    public static AgentFunctionCall FromJson(
        string name,
        string argumentsJson,
        string? callId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new AgentFunctionCall(name, string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson, callId);
    }
}

/// <summary>
/// Serializes and parses canonical function-call envelopes.
/// Parsers accept both the canonical schema and the legacy aliases already
/// supported by the framework middleware.
/// </summary>
public static class AgentFunctionCallSerializer
{
    /// <summary>
    /// Serializes a call to the canonical framework envelope.
    /// </summary>
    public static string Serialize(AgentFunctionCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentException.ThrowIfNullOrWhiteSpace(call.Name);

        using var argumentsDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var envelope = new Dictionary<string, object?>
        {
            ["type"] = "function_call",
            ["id"] = call.CallId,
            ["name"] = call.Name,
            ["arguments"] = argumentsDocument.RootElement.Clone(),
        };

        return JsonSerializer.Serialize(envelope);
    }

    /// <summary>
    /// Attempts to parse a canonical or legacy function-call envelope.
    /// </summary>
    public static bool TryParse(string content, out AgentFunctionCall call)
    {
        call = default!;

        if (string.IsNullOrWhiteSpace(content))
            return false;

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var root = document.RootElement;
            if (!TryGetNameAndArguments(root, out var name, out var argumentsJson))
                return false;

            var type = TryGetString(root, "type");
            if (!string.IsNullOrWhiteSpace(type) &&
                !string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(type, "tool_call", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            call = new AgentFunctionCall(name, argumentsJson, TryGetString(root, "id"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetNameAndArguments(
        JsonElement root,
        out string name,
        out string argumentsJson)
    {
        name = string.Empty;
        argumentsJson = "{}";

        if (root.TryGetProperty("function", out var functionElement) &&
            functionElement.ValueKind == JsonValueKind.Object)
        {
            var nestedName = TryGetString(functionElement, "name");
            if (string.IsNullOrWhiteSpace(nestedName))
                return false;

            name = nestedName;
            argumentsJson = NormalizeArguments(functionElement, "arguments");
            return true;
        }

        name = TryGetString(root, "name") ?? TryGetString(root, "tool") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        argumentsJson = NormalizeArguments(root, "arguments");
        return true;
    }

    private static string NormalizeArguments(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var argumentsElement))
            return "{}";

        if (argumentsElement.ValueKind == JsonValueKind.String)
        {
            var raw = argumentsElement.GetString();
            return string.IsNullOrWhiteSpace(raw) ? "{}" : raw;
        }

        return argumentsElement.GetRawText();
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return null;

        return property.GetString();
    }
}