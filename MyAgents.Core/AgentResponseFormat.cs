using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace MyAgents.Core;

/// <summary>
/// Specifies the expected response format for an agent turn.
/// </summary>
public abstract record AgentResponseFormat
{
    /// <summary>Unconstrained free-form text response.</summary>
    public static AgentResponseFormat Text { get; } = new TextFormat();

    /// <summary>Generic JSON object response without an enforced schema.</summary>
    public static AgentResponseFormat Json { get; } = new JsonObjectFormat();

    /// <summary>
    /// Schema-constrained JSON response using the specified JSON schema string.
    /// </summary>
    /// <param name="schemaName">Name of the schema or model structure.</param>
    /// <param name="schemaJson">The JSON schema string.</param>
    /// <param name="schemaDescription">Optional human-readable description of the schema.</param>
    /// <param name="strict">Whether strict schema adherence is requested from the model.</param>
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
    /// Generates a schema-constrained JSON response format for the specified type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The target type whose JSON schema will be exported.</typeparam>
    /// <param name="schemaName">Optional schema name. Defaults to the type name.</param>
    /// <param name="schemaDescription">Optional description of the schema.</param>
    /// <param name="options">Optional JSON serializer options to control schema generation.</param>
    /// <param name="strict">Whether strict schema adherence is requested.</param>
    public static AgentResponseFormat ForType<T>(
        string? schemaName = null,
        string? schemaDescription = null,
        JsonSerializerOptions? options = null,
        bool? strict = null)
        => ForType(typeof(T), schemaName, schemaDescription, options, strict);

    /// <summary>
    /// Generates a schema-constrained JSON response format for the specified type.
    /// </summary>
    /// <param name="type">The target type whose JSON schema will be exported.</param>
    /// <param name="schemaName">Optional schema name. Defaults to the type name.</param>
    /// <param name="schemaDescription">Optional description of the schema.</param>
    /// <param name="options">Optional JSON serializer options to control schema generation.</param>
    /// <param name="strict">Whether strict schema adherence is requested.</param>
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
        bool? Strict = null) : AgentResponseFormat;
}
