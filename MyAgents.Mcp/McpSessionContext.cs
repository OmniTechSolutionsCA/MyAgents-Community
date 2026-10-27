using System.Collections;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Client;
using MyAgents.Core;
using MyAgents.Core.Prompts;
using MyAgents.Core.Tools;

namespace MyAgents.Mcp;

/// <summary>
/// MCP tool metadata exposed through the session context.
/// </summary>
public sealed record McpToolDescriptor(string Name, string? Description);

/// <summary>
/// MCP prompt metadata exposed through the session context.
/// </summary>
public sealed record McpPromptDescriptor(
    string Name,
    string? Description,
    IReadOnlyList<string> Arguments);

/// <summary>
/// MCP resource metadata exposed through the session context.
/// </summary>
public sealed record McpResourceDescriptor(
    string Reference,
    string Uri,
    string? Name,
    string? Description,
    string? MimeType);

/// <summary>
/// Resolved MCP prompt content.
/// </summary>
public sealed record McpPromptResolution(
    string Name,
    string Content);

/// <summary>
/// Session-scoped access to MCP tools, prompts, and resources.
/// </summary>
public interface IMcpSessionContext
{
    ValueTask<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<McpPromptDescriptor>> ListPromptsAsync(CancellationToken cancellationToken = default);

    ValueTask<McpPromptResolution?> ResolvePromptAsync(
        string name,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<McpResourceDescriptor>> ListResourcesAsync(CancellationToken cancellationToken = default);

    ValueTask<string> ReadResourceAsync(string reference, CancellationToken cancellationToken = default);
}

internal sealed record McpServerRegistration(
    string? Prefix,
    string DisplayName,
    McpClient Client,
    IReadOnlyList<McpToolDescriptor> Tools);

internal sealed class McpSessionContext : IMcpSessionContext
{
    private readonly IReadOnlyList<McpServerRegistration> _servers;

    public McpSessionContext(IReadOnlyList<McpServerRegistration> servers)
    {
        _servers = servers ?? throw new ArgumentNullException(nameof(servers));
    }

    public ValueTask<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<McpToolDescriptor>>(
            _servers.SelectMany(static server => server.Tools).ToArray());

    public async ValueTask<IReadOnlyList<McpPromptDescriptor>> ListPromptsAsync(CancellationToken cancellationToken = default)
    {
        var prompts = new List<McpPromptDescriptor>();
        foreach (var server in _servers)
        {
            var result = await McpReflection.InvokeAsync(
                server.Client,
                "ListPromptsAsync",
                cancellationToken).ConfigureAwait(false);
            if (result is null)
                continue;

            foreach (var item in McpReflection.GetSequence(result, "Prompts"))
            {
                var name = McpReflection.GetString(item, "Name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var arguments = McpReflection.GetSequence(item, "Arguments")
                    .Select(argument => McpReflection.GetString(argument, "Name"))
                    .Where(static argumentName => !string.IsNullOrWhiteSpace(argumentName))
                    .Cast<string>()
                    .ToArray();

                prompts.Add(new McpPromptDescriptor(
                    McpReflection.ApplyPrefix(server.Prefix, name),
                    McpReflection.GetString(item, "Description"),
                    arguments));
            }
        }

        return prompts;
    }

    public async ValueTask<McpPromptResolution?> ResolvePromptAsync(
        string name,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var (server, unprefixedName) = ResolvePromptServer(name);
        if (server is null)
            return null;

        var result = await McpReflection.InvokeAsync(
            server.Client,
            "GetPromptAsync",
            cancellationToken,
            new Dictionary<string, object?>
            {
                ["name"] = unprefixedName,
                ["arguments"] = arguments?.ToDictionary(static pair => pair.Key, static pair => (object?)pair.Value),
            }).ConfigureAwait(false);

        if (result is null)
            return null;

        return new McpPromptResolution(name, McpReflection.FlattenPrompt(result));
    }

    public async ValueTask<IReadOnlyList<McpResourceDescriptor>> ListResourcesAsync(CancellationToken cancellationToken = default)
    {
        var resources = new List<McpResourceDescriptor>();
        foreach (var server in _servers)
        {
            var result = await McpReflection.InvokeAsync(
                server.Client,
                "ListResourcesAsync",
                cancellationToken).ConfigureAwait(false);
            if (result is null)
                continue;

            foreach (var item in McpReflection.GetSequence(result, "Resources"))
            {
                var uri = McpReflection.GetString(item, "Uri");
                if (string.IsNullOrWhiteSpace(uri))
                    continue;

                resources.Add(new McpResourceDescriptor(
                    Reference: McpReflection.BuildReference(server.Prefix, uri),
                    Uri: uri,
                    Name: McpReflection.GetString(item, "Name"),
                    Description: McpReflection.GetString(item, "Description"),
                    MimeType: McpReflection.GetString(item, "MimeType")));
            }
        }

        return resources;
    }

    public async ValueTask<string> ReadResourceAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        var (server, uri) = ResolveResourceServer(reference);
        if (server is null)
            throw new InvalidOperationException($"MCP resource '{reference}' was not found.");

        var result = await McpReflection.InvokeAsync(
            server.Client,
            "ReadResourceAsync",
            cancellationToken,
            new Dictionary<string, object?>
            {
                ["uri"] = uri,
            }).ConfigureAwait(false);

        if (result is null)
            throw new InvalidOperationException($"MCP resource '{reference}' could not be read.");

        return McpReflection.FlattenResource(result);
    }

    public async ValueTask ImportPromptsAsync(PromptRegistry registry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var prompts = await ListPromptsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var prompt in prompts)
        {
            var resolved = await ResolvePromptAsync(prompt.Name, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (resolved is null || string.IsNullOrWhiteSpace(resolved.Content))
                continue;

            registry.Register(new PromptTemplateDefinition(
                Name: prompt.Name,
                Version: "1.0",
                Template: resolved.Content,
                Description: prompt.Description));
        }
    }

    private (McpServerRegistration? Server, string UnprefixedName) ResolvePromptServer(string name)
    {
        foreach (var server in _servers)
        {
            if (string.IsNullOrWhiteSpace(server.Prefix))
            {
                if (_servers.Count == 1)
                    return (server, name);
                continue;
            }

            var prefix = $"{server.Prefix}_";
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (server, name[prefix.Length..]);
        }

        return _servers.Count == 1 ? (_servers[0], name) : (null, name);
    }

    private (McpServerRegistration? Server, string Uri) ResolveResourceServer(string reference)
    {
        foreach (var server in _servers)
        {
            if (McpReflection.TryParseReference(server.Prefix, reference, out var uri))
                return (server, uri);
        }

        return _servers.Count == 1 ? (_servers[0], reference) : (null, reference);
    }
}

internal sealed class McpListResourcesTool : ITool
{
    private readonly IMcpSessionContext _context;
    private readonly string? _serverPrefix;

    public McpListResourcesTool(IMcpSessionContext context, string? prefix = null, string? serverPrefix = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _serverPrefix = string.IsNullOrWhiteSpace(serverPrefix) ? null : serverPrefix;
        Name = string.IsNullOrWhiteSpace(prefix) ? "mcp_list_resources" : $"{prefix}_mcp_list_resources";
    }

    public string Name { get; }

    public string? Description => "Lists MCP resources exposed through the current session context.";

    public async ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        var resources = await _context.ListResourcesAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(_serverPrefix))
        {
            resources = resources
                .Where(resource => resource.Reference.StartsWith($"{_serverPrefix}::", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        return JsonSerializer.Serialize(resources);
    }
}

internal sealed class McpReadResourceTool : ITool
{
    private readonly IMcpSessionContext _context;
    private readonly string? _serverPrefix;

    public McpReadResourceTool(IMcpSessionContext context, string? prefix = null, string? serverPrefix = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _serverPrefix = string.IsNullOrWhiteSpace(serverPrefix) ? null : serverPrefix;
        Name = string.IsNullOrWhiteSpace(prefix) ? "mcp_read_resource" : $"{prefix}_mcp_read_resource";
    }

    public string Name { get; }

    public string? Description => "Reads an MCP resource by reference from the current session context.";

    public async ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, string?>>(jsonArguments)
            ?? throw new InvalidOperationException("Failed to parse MCP resource tool arguments.");
        var reference = args.TryGetValue("reference", out var value) ? value : null;
        if (string.IsNullOrWhiteSpace(reference))
            throw new InvalidOperationException("The 'reference' argument is required.");
        if (!string.IsNullOrWhiteSpace(_serverPrefix) &&
            !reference.StartsWith($"{_serverPrefix}::", StringComparison.OrdinalIgnoreCase))
        {
            reference = $"{_serverPrefix}::{reference}";
        }

        var content = await _context.ReadResourceAsync(reference, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { reference, content });
    }
}

internal static class McpReflection
{
    public static async ValueTask<object?> InvokeAsync(
        object target,
        string methodName,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, object?>? namedArguments = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);

        var method = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method => string.Equals(method.Name, methodName, StringComparison.Ordinal));
        if (method is null)
            return null;

        var invocationArguments = BuildInvocationArguments(method, cancellationToken, namedArguments);
        var invocationResult = method.Invoke(target, invocationArguments);
        return await AwaitAsync(invocationResult).ConfigureAwait(false);
    }

    public static IEnumerable<object> GetSequence(object source, string propertyName)
    {
        var property = source.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        if (property?.GetValue(source) is not IEnumerable sequence)
            return Array.Empty<object>();

        var items = new List<object>();
        foreach (var item in sequence)
        {
            if (item is not null)
                items.Add(item);
        }

        return items;
    }

    public static string? GetString(object source, string propertyName)
        => source.GetType()
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?
            .GetValue(source)?
            .ToString();

    public static string FlattenPrompt(object result)
    {
        var messages = GetSequence(result, "Messages")
            .Select(message =>
            {
                var role = GetString(message, "Role");
                var content = GetString(message, "Content");
                if (string.IsNullOrWhiteSpace(content))
                {
                    var contentObject = message.GetType().GetProperty("Content", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(message);
                    content = contentObject is null ? null : JsonSerializer.Serialize(contentObject);
                }

                return string.IsNullOrWhiteSpace(role)
                    ? content
                    : $"[{role}] {content}";
            })
            .Where(static value => !string.IsNullOrWhiteSpace(value));

        var flattened = string.Join(Environment.NewLine, messages);
        return string.IsNullOrWhiteSpace(flattened)
            ? JsonSerializer.Serialize(result)
            : flattened;
    }

    public static string FlattenResource(object result)
    {
        var contents = GetSequence(result, "Contents");
        var text = string.Join(
            Environment.NewLine,
            contents
                .Select(item => GetString(item, "Text") ?? GetString(item, "Uri"))
                .Where(static value => !string.IsNullOrWhiteSpace(value)));

        return string.IsNullOrWhiteSpace(text)
            ? JsonSerializer.Serialize(result)
            : text;
    }

    public static string ApplyPrefix(string? prefix, string value)
        => string.IsNullOrWhiteSpace(prefix) ? value : $"{prefix}_{value}";

    public static string BuildReference(string? prefix, string uri)
        => string.IsNullOrWhiteSpace(prefix) ? uri : $"{prefix}::{uri}";

    public static bool TryParseReference(string? prefix, string reference, out string uri)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            uri = reference;
            return true;
        }

        var token = $"{prefix}::";
        if (reference.StartsWith(token, StringComparison.OrdinalIgnoreCase))
        {
            uri = reference[token.Length..];
            return true;
        }

        uri = string.Empty;
        return false;
    }

    private static object?[] BuildInvocationArguments(
        MethodInfo method,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, object?>? namedArguments)
    {
        var arguments = new object?[method.GetParameters().Length];
        for (var index = 0; index < arguments.Length; index++)
        {
            var parameter = method.GetParameters()[index];
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                arguments[index] = cancellationToken;
                continue;
            }

            if (namedArguments is not null)
            {
                if (namedArguments.TryGetValue(parameter.Name ?? string.Empty, out var directValue))
                {
                    arguments[index] = directValue;
                    continue;
                }

                if (TryCreateComplexArgument(parameter.ParameterType, namedArguments, out var complexValue))
                {
                    arguments[index] = complexValue;
                    continue;
                }
            }

            arguments[index] = parameter.HasDefaultValue
                ? parameter.DefaultValue
                : parameter.ParameterType.IsValueType
                    ? Activator.CreateInstance(parameter.ParameterType)
                    : null;
        }

        return arguments;
    }

    private static bool TryCreateComplexArgument(
        Type parameterType,
        IReadOnlyDictionary<string, object?> namedArguments,
        out object? value)
    {
        value = null;
        if (parameterType == typeof(string) ||
            typeof(IEnumerable).IsAssignableFrom(parameterType) ||
            parameterType.IsPrimitive)
        {
            return false;
        }

        var instance = Activator.CreateInstance(parameterType);
        if (instance is null)
            return false;

        var assigned = false;
        foreach (var pair in namedArguments)
        {
            var property = parameterType.GetProperty(pair.Key, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property is null || !property.CanWrite)
                continue;

            property.SetValue(instance, pair.Value);
            assigned = true;
        }

        value = assigned ? instance : null;
        return assigned;
    }

    private static async ValueTask<object?> AwaitAsync(object? invocationResult)
    {
        if (invocationResult is null)
            return null;

        switch (invocationResult)
        {
            case Task task:
                await task.ConfigureAwait(false);
                return task.GetType().GetProperty("Result")?.GetValue(task);
            default:
                var resultType = invocationResult.GetType();
                if (resultType == typeof(ValueTask))
                {
                    await (ValueTask)invocationResult;
                    return null;
                }

                if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(ValueTask<>))
                    return await AwaitDynamicAsync((dynamic)invocationResult).ConfigureAwait(false);

                return invocationResult;
        }
    }

    private static async Task<object?> AwaitDynamicAsync(dynamic value)
        => await value;
}
