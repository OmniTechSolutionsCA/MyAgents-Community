using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace MyAgents.Core.Tools;

/// <summary>
/// An <see cref="ITool"/> implementation backed by a C# <see cref="Delegate"/> (such as a method, lambda, or local function)
/// with automatic JSON schema generation, compiled invocation, and zero-reflection argument deserialization.
/// </summary>
public sealed class DelegateTool : ITool
{
    private delegate ValueTask<string> ResultFormatter(object? rawResult, JsonSerializerOptions options);

    private readonly Func<object?[], object?> _invoker;
    private readonly ParameterDescriptor[] _parameters;
    private readonly ResultFormatter _formatter;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string? Description { get; }

    /// <inheritdoc />
    public string? ParametersSchemaJson { get; }

    private DelegateTool(
        Func<object?[], object?> invoker,
        ParameterDescriptor[] parameters,
        ResultFormatter formatter,
        string name,
        string? description,
        string parametersSchemaJson,
        JsonSerializerOptions jsonOptions)
    {
        _invoker = invoker;
        _parameters = parameters;
        _formatter = formatter;
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
        var rawParams = method.Method.GetParameters();

        var properties = new JsonObject();
        var required = new JsonArray();
        var nullabilityContext = new NullabilityInfoContext();
        var descriptors = new ParameterDescriptor[rawParams.Length];

        for (var i = 0; i < rawParams.Length; i++)
        {
            var param = rawParams[i];
            var isCt = param.ParameterType == typeof(CancellationToken);
            var isReq = !isCt && IsParameterRequired(param, nullabilityContext);
            var paramName = param.Name ?? $"arg{i}";

            descriptors[i] = new ParameterDescriptor
            {
                Parameter = param,
                Name = paramName,
                ParameterType = param.ParameterType,
                IsCancellationToken = isCt,
                IsRequired = isReq,
                HasDefaultValue = param.HasDefaultValue,
                DefaultValue = param.HasDefaultValue ? param.DefaultValue : null,
            };

            if (isCt)
                continue;

            var paramSchema = JsonSchemaExporter.GetJsonSchemaAsNode(options, param.ParameterType);

            var paramDesc = param.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (!string.IsNullOrWhiteSpace(paramDesc) && paramSchema is JsonObject schemaObj)
            {
                schemaObj["description"] = paramDesc;
            }

            properties[paramName] = paramSchema;

            if (isReq)
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
        var invoker = CompileInvoker(method, descriptors);
        var formatter = CreateResultFormatter(method.Method.ReturnType);

        return new DelegateTool(invoker, descriptors, formatter, toolName, toolDescription, schemaJson, options);
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

            for (var i = 0; i < _parameters.Length; i++)
            {
                var descriptor = _parameters[i];

                if (descriptor.IsCancellationToken)
                {
                    args[i] = cancellationToken;
                    continue;
                }

                JsonElement? matchedElement = null;

                if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty(descriptor.Name, out var exactProp))
                    {
                        matchedElement = exactProp;
                    }
                    else
                    {
                        foreach (var prop in root.EnumerateObject())
                        {
                            if (string.Equals(prop.Name, descriptor.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                matchedElement = prop.Value;
                                break;
                            }
                        }
                    }
                }

                if (matchedElement.HasValue)
                {
                    args[i] = matchedElement.Value.Deserialize(descriptor.ParameterType, _jsonOptions);
                }
                else if (descriptor.HasDefaultValue)
                {
                    args[i] = descriptor.DefaultValue;
                }
                else if (descriptor.IsRequired)
                {
                    throw new ArgumentException($"Missing required argument '{descriptor.Name}' for tool '{Name}'.");
                }
                else
                {
                    args[i] = null;
                }
            }

            var invocationResult = _invoker(args);
            return await _formatter(invocationResult, _jsonOptions).ConfigureAwait(false);
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

    private static Func<object?[], object?> CompileInvoker(Delegate method, ParameterDescriptor[] parameters)
    {
        try
        {
            var argsParam = Expression.Parameter(typeof(object?[]), "args");
            var callArgs = new Expression[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var arrayAccess = Expression.ArrayAccess(argsParam, Expression.Constant(i));
                callArgs[i] = Expression.Convert(arrayAccess, parameters[i].ParameterType);
            }

            var targetExpr = method.Target is not null ? Expression.Constant(method.Target) : null;
            Expression body = Expression.Call(targetExpr, method.Method, callArgs);

            if (method.Method.ReturnType == typeof(void))
            {
                body = Expression.Block(body, Expression.Constant(null, typeof(object)));
            }
            else if (method.Method.ReturnType.IsValueType)
            {
                body = Expression.Convert(body, typeof(object));
            }

            return Expression.Lambda<Func<object?[], object?>>(body, argsParam).Compile();
        }
        catch
        {
            return args => method.DynamicInvoke(args);
        }
    }

    private static ResultFormatter CreateResultFormatter(Type returnType)
    {
        if (returnType == typeof(void))
        {
            return static (_, _) => new ValueTask<string>("null");
        }

        if (returnType == typeof(string))
        {
            return static (raw, _) => new ValueTask<string>((string?)raw ?? "null");
        }

        if (returnType == typeof(Task))
        {
            return static async (raw, _) =>
            {
                if (raw is Task task)
                    await task.ConfigureAwait(false);
                return "true";
            };
        }

        if (returnType == typeof(ValueTask))
        {
            return static async (raw, _) =>
            {
                if (raw is ValueTask vTask)
                    await vTask.ConfigureAwait(false);
                return "true";
            };
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            if (resultType == typeof(string))
            {
                return static async (raw, _) =>
                {
                    var task = (Task<string?>)raw!;
                    var res = await task.ConfigureAwait(false);
                    return res ?? "null";
                };
            }

            var method = typeof(DelegateTool).GetMethod(nameof(FormatGenericTaskResult), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(resultType);
            return (ResultFormatter)method.CreateDelegate(typeof(ResultFormatter));
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            if (resultType == typeof(string))
            {
                return static async (raw, _) =>
                {
                    var task = (ValueTask<string?>)raw!;
                    var res = await task.ConfigureAwait(false);
                    return res ?? "null";
                };
            }

            var method = typeof(DelegateTool).GetMethod(nameof(FormatGenericValueTaskResult), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(resultType);
            return (ResultFormatter)method.CreateDelegate(typeof(ResultFormatter));
        }

        return static (raw, options) =>
        {
            if (raw is null) return new ValueTask<string>("null");
            if (raw is string str) return new ValueTask<string>(str);
            return new ValueTask<string>(JsonSerializer.Serialize(raw, options));
        };
    }

    private static async ValueTask<string> FormatGenericTaskResult<T>(object? raw, JsonSerializerOptions options)
    {
        var task = (Task<T>)raw!;
        var result = await task.ConfigureAwait(false);
        if (result is null) return "null";
        if (result is string s) return s;
        return JsonSerializer.Serialize(result, options);
    }

    private static async ValueTask<string> FormatGenericValueTaskResult<T>(object? raw, JsonSerializerOptions options)
    {
        var vTask = (ValueTask<T>)raw!;
        var result = await vTask.ConfigureAwait(false);
        if (result is null) return "null";
        if (result is string s) return s;
        return JsonSerializer.Serialize(result, options);
    }

    private sealed class ParameterDescriptor
    {
        public required ParameterInfo Parameter { get; init; }
        public required string Name { get; init; }
        public required Type ParameterType { get; init; }
        public required bool IsCancellationToken { get; init; }
        public required bool IsRequired { get; init; }
        public required bool HasDefaultValue { get; init; }
        public required object? DefaultValue { get; init; }
    }
}
