using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace MyAgents.Core;

/// <summary>
/// Specifies the expected response format for an agent turn or session.
/// </summary>
public abstract record AgentResponseFormat
{
    /// <summary>Unconstrained free-form text response (default).</summary>
    public static AgentResponseFormat Text { get; } = new TextFormat();

    /// <summary>Generic JSON object response without a schema constraint.</summary>
    public static AgentResponseFormat JsonObject { get; } = new JsonObjectFormat();

    /// <summary>
    /// Creates a schema-constrained JSON response format with a pre-rendered JSON schema string.
    /// </summary>
    /// <param name="schemaName">Name identifying the schema.</param>
    /// <param name="schemaJson">JSON Schema definition string.</param>
    /// <param name="schemaDescription">Optional human-readable description of the schema.</param>
    /// <param name="strict">Whether the model provider should enforce strict schema adherence.</param>
    public static AgentResponseFormat ForJsonSchema(
        string schemaName,
        string schemaJson,
        string? schemaDescription = null,
        bool? strict = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaJson);
        return new JsonSchemaFormat(schemaName, schemaJson, schemaDescription, strict);
    }

    /// <summary>
    /// Creates a schema-constrained JSON response format for a strongly-typed .NET model type.
    /// </summary>
    /// <typeparam name="T">The type to generate a JSON schema for.</typeparam>
    /// <param name="schemaName">Optional schema name. Defaults to typeof(T).Name.</param>
    /// <param name="schemaDescription">Optional human-readable description of the schema.</param>
    /// <param name="options">Optional JSON serializer options for schema generation.</param>
    /// <param name="strict">Whether the model provider should enforce strict schema adherence.</param>
    public static AgentResponseFormat ForType<T>(
        string? schemaName = null,
        string? schemaDescription = null,
        JsonSerializerOptions? options = null,
        bool? strict = null)
        => ForType(typeof(T), schemaName, schemaDescription, options, strict);

    /// <summary>
    /// Creates a schema-constrained JSON response format for a given .NET type.
    /// </summary>
    /// <param name="type">The type to generate a JSON schema for.</param>
    /// <param name="schemaName">Optional schema name. Defaults to type.Name.</param>
    /// <param name="schemaDescription">Optional human-readable description of the schema.</param>
    /// <param name="options">Optional JSON serializer options for schema generation.</param>
    /// <param name="strict">Whether the model provider should enforce strict schema adherence.</param>
    public static AgentResponseFormat ForType(
        Type type,
        string? schemaName = null,
        string? schemaDescription = null,
        JsonSerializerOptions? options = null,
        bool? strict = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        var opts = options ?? JsonSerializerOptions.Default;
        var node = JsonSchemaExporter.GetJsonSchemaAsNode(opts, type);
        var name = schemaName ?? type.Name;
        return new JsonSchemaFormat(name, node.ToJsonString(), schemaDescription, strict);
    }

    /// <summary>Unconstrained free-form text response.</summary>
    public sealed record TextFormat : AgentResponseFormat;

    /// <summary>Generic JSON object response without an enforced schema.</summary>
    public sealed record JsonObjectFormat : AgentResponseFormat;

    /// <summary>Schema-constrained JSON response.</summary>
    public sealed record JsonSchemaFormat(
        string SchemaName,
        string SchemaJson,
        string? SchemaDescription = null,
        bool? Strict = null) : AgentResponseFormat
    {
        private JsonElement? _parsedSchema;

        /// <summary>
        /// Gets the parsed JSON schema element, deserialized once and cached for reuse.
        /// </summary>
        public JsonElement ParsedSchema => _parsedSchema ??= JsonSerializer.Deserialize<JsonElement>(SchemaJson);
    }
}
