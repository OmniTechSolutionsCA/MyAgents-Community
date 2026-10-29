using System.ComponentModel;
using System.Globalization;
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
    private delegate ValueTask<string> DirectInvoker(
        JsonElement root,
        bool hasRoot,
        CancellationToken cancellationToken,
        JsonSerializerOptions options);

    private readonly DirectInvoker _directInvoker;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string? Description { get; }

    /// <inheritdoc />
    public string? ParametersSchemaJson { get; }

    private DelegateTool(
        DirectInvoker directInvoker,
        string name,
        string? description,
        string parametersSchemaJson,
        JsonSerializerOptions jsonOptions)
    {
        _directInvoker = directInvoker;
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
        var directInvoker = CompileDirectInvoker(method, descriptors, toolName);

        return new DelegateTool(directInvoker, toolName, toolDescription, schemaJson, options);
    }

    /// <inheritdoc />
    public async ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jsonArguments))
        {
            return await _directInvoker(default, false, cancellationToken, _jsonOptions).ConfigureAwait(false);
        }

        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(jsonArguments);
        }
        catch (JsonException)
        {
            // Fallback to empty document if argument string is not valid JSON
        }

        using (doc)
        {
            var root = doc is not null ? doc.RootElement : default;
            var hasRoot = doc is not null;
            return await _directInvoker(root, hasRoot, cancellationToken, _jsonOptions).ConfigureAwait(false);
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

    private static DirectInvoker CompileDirectInvoker(
        Delegate method,
        ParameterDescriptor[] parameters,
        string toolName)
    {
        try
        {
            var rootParam = Expression.Parameter(typeof(JsonElement), "root");
            var hasRootParam = Expression.Parameter(typeof(bool), "hasRoot");
            var ctParam = Expression.Parameter(typeof(CancellationToken), "cancellationToken");
            var optionsParam = Expression.Parameter(typeof(JsonSerializerOptions), "options");

            var callArgs = new Expression[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (p.IsCancellationToken)
                {
                    callArgs[i] = ctParam;
                }
                else if (p.ParameterType == typeof(int))
                {
                    var m = typeof(DelegateTool).GetMethod(nameof(GetArgumentInt32), BindingFlags.NonPublic | BindingFlags.Static)!;
                    callArgs[i] = Expression.Call(m, rootParam, hasRootParam,
                        Expression.Constant(p.Name),
                        Expression.Constant(p.HasDefaultValue),
                        Expression.Constant(p.DefaultValue is int di ? di : 0),
                        Expression.Constant(p.IsRequired),
                        Expression.Constant(toolName),
                        optionsParam);
                }
                else if (p.ParameterType == typeof(long))
                {
                    var m = typeof(DelegateTool).GetMethod(nameof(GetArgumentInt64), BindingFlags.NonPublic | BindingFlags.Static)!;
                    callArgs[i] = Expression.Call(m, rootParam, hasRootParam,
                        Expression.Constant(p.Name),
                        Expression.Constant(p.HasDefaultValue),
                        Expression.Constant(p.DefaultValue is long dl ? dl : 0L),
                        Expression.Constant(p.IsRequired),
                        Expression.Constant(toolName),
                        optionsParam);
                }
                else if (p.ParameterType == typeof(bool))
                {
                    var m = typeof(DelegateTool).GetMethod(nameof(GetArgumentBoolean), BindingFlags.NonPublic | BindingFlags.Static)!;
                    callArgs[i] = Expression.Call(m, rootParam, hasRootParam,
                        Expression.Constant(p.Name),
                        Expression.Constant(p.HasDefaultValue),
                        Expression.Constant(p.DefaultValue is bool db && db),
                        Expression.Constant(p.IsRequired),
                        Expression.Constant(toolName),
                        optionsParam);
                }
                else if (p.ParameterType == typeof(double))
                {
                    var m = typeof(DelegateTool).GetMethod(nameof(GetArgumentDouble), BindingFlags.NonPublic | BindingFlags.Static)!;
                    callArgs[i] = Expression.Call(m, rootParam, hasRootParam,
                        Expression.Constant(p.Name),
                        Expression.Constant(p.HasDefaultValue),
                        Expression.Constant(p.DefaultValue is double dd ? dd : 0.0),
                        Expression.Constant(p.IsRequired),
                        Expression.Constant(toolName),
                        optionsParam);
                }
                else if (p.ParameterType == typeof(float))
                {
                    var m = typeof(DelegateTool).GetMethod(nameof(GetArgumentSingle), BindingFlags.NonPublic | BindingFlags.Static)!;
                    callArgs[i] = Expression.Call(m, rootParam, hasRootParam,
                        Expression.Constant(p.Name),
                        Expression.Constant(p.HasDefaultValue),
                        Expression.Constant(p.DefaultValue is float df ? df : 0.0f),
                        Expression.Constant(p.IsRequired),
                        Expression.Constant(toolName),
                        optionsParam);
                }
                else if (p.ParameterType == typeof(string))
                {
                    var m = typeof(DelegateTool).GetMethod(nameof(GetArgumentString), BindingFlags.NonPublic | BindingFlags.Static)!;
                    callArgs[i] = Expression.Call(m, rootParam, hasRootParam,
                        Expression.Constant(p.Name),
                        Expression.Constant(p.HasDefaultValue),
                        Expression.Constant(p.DefaultValue as string, typeof(string)),
                        Expression.Constant(p.IsRequired),
                        Expression.Constant(toolName),
                        optionsParam);
                }
                else
                {
                    var m = typeof(DelegateTool).GetMethod(nameof(GetArgumentComplex), BindingFlags.NonPublic | BindingFlags.Static)!
                        .MakeGenericMethod(p.ParameterType);
                    callArgs[i] = Expression.Call(m, rootParam, hasRootParam,
                        Expression.Constant(p.Name),
                        Expression.Constant(p.HasDefaultValue),
                        Expression.Constant(p.DefaultValue, p.ParameterType),
                        Expression.Constant(p.IsRequired),
                        Expression.Constant(toolName),
                        optionsParam);
                }
            }

            var targetExpr = method.Target is not null ? Expression.Constant(method.Target) : null;
            Expression callExpr = Expression.Call(targetExpr, method.Method, callArgs);
            var returnType = method.Method.ReturnType;
            Expression body;

            if (returnType == typeof(void))
            {
                body = Expression.Block(callExpr, Expression.Constant(new ValueTask<string>("null")));
            }
            else if (returnType == typeof(string))
            {
                var m = typeof(DelegateTool).GetMethod(nameof(FormatStringResult), BindingFlags.NonPublic | BindingFlags.Static)!;
                body = Expression.Call(m, callExpr);
            }
            else if (returnType == typeof(int))
            {
                var m = typeof(DelegateTool).GetMethod(nameof(FormatInt32Result), BindingFlags.NonPublic | BindingFlags.Static)!;
                body = Expression.Call(m, callExpr);
            }
            else if (returnType == typeof(long))
            {
                var m = typeof(DelegateTool).GetMethod(nameof(FormatInt64Result), BindingFlags.NonPublic | BindingFlags.Static)!;
                body = Expression.Call(m, callExpr);
            }
            else if (returnType == typeof(bool))
            {
                var m = typeof(DelegateTool).GetMethod(nameof(FormatBooleanResult), BindingFlags.NonPublic | BindingFlags.Static)!;
                body = Expression.Call(m, callExpr);
            }
            else if (returnType == typeof(double))
            {
                var m = typeof(DelegateTool).GetMethod(nameof(FormatDoubleResult), BindingFlags.NonPublic | BindingFlags.Static)!;
                body = Expression.Call(m, callExpr);
            }
            else if (returnType == typeof(float))
            {
                var m = typeof(DelegateTool).GetMethod(nameof(FormatSingleResult), BindingFlags.NonPublic | BindingFlags.Static)!;
                body = Expression.Call(m, callExpr);
            }
            else if (returnType == typeof(ValueTask<string>))
            {
                body = callExpr;
            }
            else if (returnType == typeof(Task<string>))
            {
                var m = typeof(DelegateTool).GetMethod(nameof(FormatTaskStringResult), BindingFlags.NonPublic | BindingFlags.Static)!;
                body = Expression.Call(m, callExpr);
            }
            else if (returnType == typeof(ValueTask))
            {
                var m = typeof(DelegateTool).GetMethod(nameof(FormatValueTaskResult), BindingFlags.NonPublic | BindingFlags.Static)!;
                body = Expression.Call(m, callExpr);
            }
            else if (returnType == typeof(Task))
            {
                var m = typeof(DelegateTool).GetMethod(nameof(FormatTaskResult), BindingFlags.NonPublic | BindingFlags.Static)!;
                body = Expression.Call(m, callExpr);
            }
            else if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            {
                var resType = returnType.GetGenericArguments()[0];
                var m = typeof(DelegateTool).GetMethod(nameof(FormatValueTaskGeneric), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(resType);
                body = Expression.Call(m, callExpr, optionsParam);
            }
            else if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resType = returnType.GetGenericArguments()[0];
                var m = typeof(DelegateTool).GetMethod(nameof(FormatTaskGeneric), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(resType);
                body = Expression.Call(m, callExpr, optionsParam);
            }
            else
            {
                var m = typeof(DelegateTool).GetMethod(nameof(FormatObjectResult), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(returnType);
                body = Expression.Call(m, callExpr, optionsParam);
            }

            return Expression.Lambda<DirectInvoker>(body, rootParam, hasRootParam, ctParam, optionsParam).Compile();
        }
        catch
        {
            return CreateFallbackDirectInvoker(method, parameters, toolName);
        }
    }

    private static DirectInvoker CreateFallbackDirectInvoker(
        Delegate method,
        ParameterDescriptor[] parameters,
        string toolName)
    {
        return async (root, hasRoot, ct, options) =>
        {
            var args = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var descriptor = parameters[i];
                if (descriptor.IsCancellationToken)
                {
                    args[i] = ct;
                    continue;
                }

                if (hasRoot && TryFindProperty(root, descriptor.Name, out var element))
                {
                    args[i] = element.Deserialize(descriptor.ParameterType, options);
                }
                else if (descriptor.HasDefaultValue)
                {
                    args[i] = descriptor.DefaultValue;
                }
                else if (descriptor.IsRequired)
                {
                    throw new ArgumentException($"Missing required argument '{descriptor.Name}' for tool '{toolName}'.");
                }
            }

            var invocationResult = method.DynamicInvoke(args);
            if (invocationResult is null) return "null";
            if (invocationResult is string s) return s;
            if (invocationResult is Task<string?> taskStr) return (await taskStr.ConfigureAwait(false)) ?? "null";
            if (invocationResult is ValueTask<string?> vtaskStr) return (await vtaskStr.ConfigureAwait(false)) ?? "null";
            if (invocationResult is Task task) { await task.ConfigureAwait(false); return "true"; }
            if (invocationResult is ValueTask vtask) { await vtask.ConfigureAwait(false); return "true"; }
            return JsonSerializer.Serialize(invocationResult, options);
        };
    }

    private static bool TryFindProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty(name, out value))
            {
                return true;
            }

            foreach (var prop in root.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static int GetArgumentInt32(JsonElement root, bool hasRoot, string name, bool hasDefault, int defaultValue, bool isRequired, string toolName, JsonSerializerOptions options)
    {
        if (hasRoot && TryFindProperty(root, name, out var element))
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var val))
                return val;
            return element.Deserialize<int>(options);
        }

        if (hasDefault) return defaultValue;
        if (isRequired) throw new ArgumentException($"Missing required argument '{name}' for tool '{toolName}'.");
        return defaultValue;
    }

    private static long GetArgumentInt64(JsonElement root, bool hasRoot, string name, bool hasDefault, long defaultValue, bool isRequired, string toolName, JsonSerializerOptions options)
    {
        if (hasRoot && TryFindProperty(root, name, out var element))
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var val))
                return val;
            return element.Deserialize<long>(options);
        }

        if (hasDefault) return defaultValue;
        if (isRequired) throw new ArgumentException($"Missing required argument '{name}' for tool '{toolName}'.");
        return defaultValue;
    }

    private static double GetArgumentDouble(JsonElement root, bool hasRoot, string name, bool hasDefault, double defaultValue, bool isRequired, string toolName, JsonSerializerOptions options)
    {
        if (hasRoot && TryFindProperty(root, name, out var element))
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var val))
                return val;
            return element.Deserialize<double>(options);
        }

        if (hasDefault) return defaultValue;
        if (isRequired) throw new ArgumentException($"Missing required argument '{name}' for tool '{toolName}'.");
        return defaultValue;
    }

    private static float GetArgumentSingle(JsonElement root, bool hasRoot, string name, bool hasDefault, float defaultValue, bool isRequired, string toolName, JsonSerializerOptions options)
    {
        if (hasRoot && TryFindProperty(root, name, out var element))
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out var val))
                return val;
            return element.Deserialize<float>(options);
        }

        if (hasDefault) return defaultValue;
        if (isRequired) throw new ArgumentException($"Missing required argument '{name}' for tool '{toolName}'.");
        return defaultValue;
    }

    private static bool GetArgumentBoolean(JsonElement root, bool hasRoot, string name, bool hasDefault, bool defaultValue, bool isRequired, string toolName, JsonSerializerOptions options)
    {
        if (hasRoot && TryFindProperty(root, name, out var element))
        {
            if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return element.GetBoolean();
            return element.Deserialize<bool>(options);
        }

        if (hasDefault) return defaultValue;
        if (isRequired) throw new ArgumentException($"Missing required argument '{name}' for tool '{toolName}'.");
        return defaultValue;
    }

    private static string? GetArgumentString(JsonElement root, bool hasRoot, string name, bool hasDefault, string? defaultValue, bool isRequired, string toolName, JsonSerializerOptions options)
    {
        if (hasRoot && TryFindProperty(root, name, out var element))
        {
            if (element.ValueKind == JsonValueKind.String)
                return element.GetString();
            if (element.ValueKind == JsonValueKind.Null)
                return null;
            return element.Deserialize<string>(options);
        }

        if (hasDefault) return defaultValue;
        if (isRequired) throw new ArgumentException($"Missing required argument '{name}' for tool '{toolName}'.");
        return defaultValue;
    }

    private static T? GetArgumentComplex<T>(JsonElement root, bool hasRoot, string name, bool hasDefault, T? defaultValue, bool isRequired, string toolName, JsonSerializerOptions options)
    {
        if (hasRoot && TryFindProperty(root, name, out var element))
        {
            return element.Deserialize<T>(options);
        }

        if (hasDefault) return defaultValue;
        if (isRequired) throw new ArgumentException($"Missing required argument '{name}' for tool '{toolName}'.");
        return defaultValue;
    }

    private static ValueTask<string> FormatStringResult(string? result) => new(result ?? "null");
    private static ValueTask<string> FormatInt32Result(int result) => new(result.ToString(CultureInfo.InvariantCulture));
    private static ValueTask<string> FormatInt64Result(long result) => new(result.ToString(CultureInfo.InvariantCulture));
    private static ValueTask<string> FormatBooleanResult(bool result) => new(result ? "true" : "false");
    private static ValueTask<string> FormatDoubleResult(double result) => new(result.ToString(CultureInfo.InvariantCulture));
    private static ValueTask<string> FormatSingleResult(float result) => new(result.ToString(CultureInfo.InvariantCulture));

    private static async ValueTask<string> FormatTaskStringResult(Task<string?> task)
    {
        var result = await task.ConfigureAwait(false);
        return result ?? "null";
    }

    private static async ValueTask<string> FormatValueTaskResult(ValueTask task)
    {
        await task.ConfigureAwait(false);
        return "true";
    }

    private static async ValueTask<string> FormatTaskResult(Task task)
    {
        await task.ConfigureAwait(false);
        return "true";
    }

    private static async ValueTask<string> FormatValueTaskGeneric<T>(ValueTask<T> vTask, JsonSerializerOptions options)
    {
        var result = await vTask.ConfigureAwait(false);
        return FormatObject(result, options);
    }

    private static async ValueTask<string> FormatTaskGeneric<T>(Task<T> task, JsonSerializerOptions options)
    {
        var result = await task.ConfigureAwait(false);
        return FormatObject(result, options);
    }

    private static ValueTask<string> FormatObjectResult<T>(T? result, JsonSerializerOptions options)
    {
        return new ValueTask<string>(FormatObject(result, options));
    }

    private static string FormatObject<T>(T? result, JsonSerializerOptions options)
    {
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
