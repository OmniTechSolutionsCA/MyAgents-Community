using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using MyAgents.Core;
using MyAgents.Core.Tools;
using MyAgents.Runtime.Chat;
using OllamaSharp;
using OpenAI;
using System.ClientModel;

var backend = Environment.GetEnvironmentVariable("MYAGENTS_PROVIDER") ?? "ollama";
var isOllama = string.Equals(backend, "ollama", StringComparison.OrdinalIgnoreCase);
var isOpenAi = string.Equals(backend, "openai", StringComparison.OrdinalIgnoreCase);
if (!isOllama && !isOpenAi)
{
    Console.Error.WriteLine("MYAGENTS_PROVIDER must be ollama or openai.");
    return 1;
}

var endpoint = Environment.GetEnvironmentVariable("MYAGENTS_BASE_URL") ??
    (isOllama ? Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? "http://127.0.0.1:11434/" : null);
var model = Environment.GetEnvironmentVariable("MYAGENTS_MODEL") ??
    (isOllama ? Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "llama3.2" : null);
var apiKey = isOpenAi ? Environment.GetEnvironmentVariable("MYAGENTS_API_KEY") : null;

if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model) ||
    (isOpenAi && string.IsNullOrWhiteSpace(apiKey)))
{
    Console.Error.WriteLine("For an OpenAI-compatible provider, set MYAGENTS_BASE_URL, MYAGENTS_MODEL, and MYAGENTS_API_KEY.");
    return 1;
}

if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
    (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps) ||
    !string.IsNullOrEmpty(endpointUri.UserInfo) || !string.IsNullOrEmpty(endpointUri.Query) ||
    !string.IsNullOrEmpty(endpointUri.Fragment) ||
    (isOpenAi && endpointUri.Scheme != Uri.UriSchemeHttps && !endpointUri.IsLoopback))
{
    Console.Error.WriteLine("Use an absolute HTTP or HTTPS URL without credentials, a query, or a fragment; remote OpenAI-compatible endpoints require HTTPS.");
    return 1;
}

using IChatClient chatClient = isOllama
    ? new OllamaApiClient(endpointUri, model)
    : new OpenAI.Chat.ChatClient(model, new ApiKeyCredential(apiKey!),
        new OpenAIClientOptions { Endpoint = endpointUri }).AsIChatClient();
var services = new ServiceCollection();
services.AddSingleton(chatClient);
// Permit only the read-only example tool. All other tools are rejected.
services.AddSingleton<IToolApprovalPolicy>(new RuleBasedToolApprovalPolicy()
    .AllowTool(UtcClockTool.ToolName)
    .SetDefaultDecision(ToolApprovalDecision.Reject("sample", "Only the clock tool is allowed.")));
services.AddMyAgentsChatRuntime(options => options.DefaultModelId = model);

await using var provider = services.BuildServiceProvider();
var sessionFactory = provider.GetRequiredService<IAgentSessionFactory>();
await using var session = sessionFactory.CreateSession(
    "clock-example",
    new AgentDefinition("clock-assistant", [new ClockFeature()]));

try
{
    var turn = await session.InvokeAsync([
        AgentMessage.FromText("user", "Use the get_current_utc_time tool and tell me the current UTC time.")
    ]);
    foreach (var message in turn.Output)
        Console.WriteLine(message.Content);

    Console.WriteLine($"Tool calls: {turn.Telemetry.ToolCalls}");
    if (turn.Telemetry.ToolCalls == 0)
    {
        Console.Error.WriteLine("The model did not call the clock tool. Try a model that supports tool calling.");
        return 2;
    }
    return 0;
}
catch (HttpRequestException exception)
{
    Console.Error.WriteLine($"Could not reach the model provider at {endpointUri}: {exception.Message}");
    Console.Error.WriteLine("Check the provider settings and model; see this sample's README.");
    return 1;
}
catch (ClientResultException exception)
{
    Console.Error.WriteLine(exception.Status > 0
        ? $"The model provider returned HTTP {exception.Status}. Check the provider settings and model."
        : "Could not connect to the model provider. Check the endpoint, network, and TLS settings.");
    return 1;
}
catch (AggregateException exception) when (exception.Flatten().InnerExceptions.All(
    error => error is ClientResultException or HttpRequestException))
{
    Console.Error.WriteLine("Could not connect to the model provider after retries. Check the endpoint, network, and TLS settings.");
    return 1;
}

internal sealed class ClockFeature : IAgentFeature
{
    public void Contribute(AgentAssembly assembly)
    {
        assembly.Prompts.AddSystem("Use the available clock tool when asked for the current UTC time.");
        assembly.Tools.Add(new UtcClockTool());
    }
}

internal sealed class UtcClockTool : ITool
{
    public const string ToolName = "get_current_utc_time";
    public string Name => ToolName;
    public string? Description => "Returns the current date and time in UTC.";
    public string? ParametersSchemaJson => "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";

    public ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.WriteLine("[tool] Reading the local UTC clock.");
        return ValueTask.FromResult(DateTimeOffset.UtcNow.ToString("O"));
    }
}
