using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace MyAgents.Core.Tools;

/// <summary>
/// An <see cref="ITool"/> implementation backed by a C# <see cref="Delegate"/> (such as a method, lambda, or local function)
/// with automatic JSON schema generation and argument deserialization.
/// </summary>
public sealed class DelegateTool : ITool
{
    private readonly Delegate _method;
    private readonly ParameterInfo[] _parameters;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string? Description { get; }

    /// <inheritdoc />
    public string? ParametersSchemaJson { get; }

    private DelegateTool(
        Delegate method,
        string name,
        string? description,
        string parametersSchemaJson,
        JsonSerializerOptions jsonOptions)
    {
        _method = method;
        _parameters = method.Method.GetParameters();
        _jsonOptions = jsonOptions;
        Name = name;
        Description = description;
        ParametersSchemaJson = parametersSchemaJson;
    }

    /// <summary>
    /// Creates a new <see cref="DelegateTool"/> from a delegate.
    /// </summary>
    /// <param name="method">The delegate to execute when the tool is invoked.</param>
    /// <param name="name">Optional tool name. Defaults to the method name.</param>
    /// <param name="description">Optional tool description. Defaults to <see cref="DescriptionAttribute"/> if present.</param>
    /// <param name="jsonSerializerOptions">Optional JSON serializer options.</param>
    /// <returns>A new <see cref="DelegateTool"/> instance.</returns>
    public static DelegateTool Create(
        Delegate method,
        string? name = null,
        string? description = null,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(method);

        var toolName = !string.IsNullOrWhiteSpace(name)
            ? name
            : method.Method.Name;

        var toolDescription = !string.IsNullOrWhiteSpace(description)
            ? description
            : method.Method.GetCustomAttribute<DescriptionAttribute>()?.Description;

        var options = jsonSerializerOptions ?? JsonSerializerOptions.Default;
        var parameters = method.Method.GetParameters();

        var properties = new JsonObject();
        var required = new JsonArray();
        var nullabilityContext = new NullabilityInfoContext();

        foreach (var param in parameters)
        {
            if (param.ParameterType == typeof(CancellationToken))
                continue;

            var paramName = param.Name ?? "arg";
            var paramSchema = JsonSchemaExporter.GetJsonSchemaAsNode(options, param.ParameterType);

            var paramDesc = param.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (!string.IsNullOrWhiteSpace(paramDesc) && paramSchema is JsonObject schemaObj)
            {
                schemaObj["description"] = paramDesc;
            }

            properties[paramName] = paramSchema;

            if (IsParameterRequired(param, nullabilityContext))
            {
                required.Add(paramName);
            }
        }

        var rootSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (required.Count > 0)
        {
            rootSchema["required"] = required;
        }

        var schemaJson = rootSchema.ToJsonString();

        return new DelegateTool(method, toolName, toolDescription, schemaJson, options);
    }

    /// <inheritdoc />
    public async ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        JsonDocument? doc = null;
        if (!string.IsNullOrWhiteSpace(jsonArguments))
        {
            try
            {
                doc = JsonDocument.Parse(jsonArguments);
            }
            catch (JsonException)
            {
                // Fallback to empty document if argument string is not valid JSON
            }
        }

        using (doc)
        {
            var args = new object?[_parameters.Length];
            var nullabilityContext = new NullabilityInfoContext();

            for (var i = 0; i < _parameters.Length; i++)
            {
                var param = _parameters[i];

                if (param.ParameterType == typeof(CancellationToken))
                {
                    args[i] = cancellationToken;
                    continue;
                }

                var paramName = param.Name ?? "arg";
                JsonProperty? matchedProperty = null;

                if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, paramName, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedProperty = prop;
                            break;
                        }
                    }
                }

                if (matchedProperty.HasValue)
                {
                    args[i] = matchedProperty.Value.Value.Deserialize(param.ParameterType, _jsonOptions);
                }
                else if (param.HasDefaultValue)
                {
                    args[i] = param.DefaultValue;
                }
                else if (IsParameterRequired(param, nullabilityContext))
                {
                    throw new ArgumentException($"Missing required argument '{paramName}' for tool '{Name}'.");
                }
                else
                {
                    args[i] = null;
                }
            }

            var invocationResult = _method.DynamicInvoke(args);
            return await FormatResultAsync(invocationResult).ConfigureAwait(false);
        }
    }

    private static bool IsParameterRequired(ParameterInfo param, NullabilityInfoContext nullabilityContext)
    {
        if (param.HasDefaultValue)
            return false;

        if (param.ParameterType.IsValueType)
        {
            return Nullable.GetUnderlyingType(param.ParameterType) == null;
        }

        var nullability = nullabilityContext.Create(param);
        return nullability.ReadState != NullabilityState.Nullable;
    }

    private async ValueTask<string> FormatResultAsync(object? result)
    {
        if (result is null)
            return "null";

        if (result is string str)
            return str;

        if (result is Task task)
        {
            await task.ConfigureAwait(false);
            var resultProp = task.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
            if (resultProp is not null)
            {
                var taskResult = resultProp.GetValue(task);
                return taskResult switch
                {
                    null => "null",
                    string s => s,
                    _ => JsonSerializer.Serialize(taskResult, _jsonOptions),
                };
            }

            return "true";
        }

        var resultType = result.GetType();
        if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTaskMethod = resultType.GetMethod("AsTask", BindingFlags.Public | BindingFlags.Instance);
            if (asTaskMethod is not null)
            {
                var vTaskAsTask = (Task)asTaskMethod.Invoke(result, null)!;
                await vTaskAsTask.ConfigureAwait(false);
                var resultProp = vTaskAsTask.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
                var taskResult = resultProp?.GetValue(vTaskAsTask);
                return taskResult switch
                {
                    null => "null",
                    string s => s,
                    _ => JsonSerializer.Serialize(taskResult, _jsonOptions),
                };
            }
        }
        else if (result is ValueTask vTask)
        {
            await vTask.ConfigureAwait(false);
            return "true";
        }

        return JsonSerializer.Serialize(result, _jsonOptions);
    }
}
