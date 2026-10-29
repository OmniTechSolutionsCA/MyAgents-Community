using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyAgents.Core;

namespace MyAgents.Agents;

/// <summary>
/// Thrown when <see cref="CapabilityRouter"/> cannot match the LLM response to any
/// registered agent and no fallback policy is configured.
/// </summary>
public sealed class RoutingException : Exception
{
    /// <summary>The raw LLM response text that failed to match any agent name.</summary>
    public string RawResponse { get; }

    /// <inheritdoc />
    public RoutingException(string rawResponse)
        : base($"CapabilityRouter could not match the LLM response '{rawResponse}' to any registered agent.")
    {
        RawResponse = rawResponse;
    }
}

/// <summary>
/// The result of a single-hop routing decision.
/// </summary>
/// <param name="SelectedAgent">The agent chosen to handle the task.</param>
/// <param name="Reasoning">Optional LLM reasoning returned alongside the selection.</param>
public sealed record CapabilityRouterResult(
    AgentDefinition SelectedAgent,
    string? Reasoning);

/// <summary>
/// Single-hop LLM-based router. Given a task description and a set of candidate
/// <see cref="AgentDefinition"/>s, calls the LLM once to select the best agent.
/// </summary>
/// <remarks>
/// Scope: single-hop only — no multi-hop, no ensemble, no confidence scoring.
/// </remarks>
public sealed partial class CapabilityRouter
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Single agent registered; skipping LLM routing call.")]
    private static partial void LogSingleAgentSkipping(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Routing task via LLM. Candidates: {Count}")]
    private static partial void LogRoutingViaLlm(ILogger logger, int count);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "CapabilityRouter could not match LLM response '{Response}' to any agent. Using configured fallback agent '{Fallback}'.")]
    private static partial void LogUnmatchedUsingFallback(ILogger logger, string response, string fallback);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "CapabilityRouter could not match LLM response '{Response}' to any registered agent. Configure a fallback agent via the 'fallbackAgentName' constructor parameter to opt in to silent fallback.")]
    private static partial void LogUnmatchedNoFallback(ILogger logger, string response);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "CapabilityRouter selected agent '{Name}'.")]
    private static partial void LogAgentSelected(ILogger logger, string name);

    private readonly IChatClient _chatClient;
    private readonly ReadOnlyCollection<AgentDefinition> _agents;
    private readonly ILogger<CapabilityRouter> _logger;
    private readonly string? _fallbackAgentName;

    /// <param name="chatClient">The <see cref="IChatClient"/> used for the routing LLM call.</param>
    /// <param name="agents">Candidate agents the router may select from.</param>
    /// <param name="logger">Optional logger; defaults to <see cref="NullLogger"/>.</param>
    /// <param name="fallbackAgentName">
    /// When set, the named agent is selected if the LLM returns an unrecognised response
    /// (opt-in backward-compatible behaviour).
    /// When <see langword="null"/> (default) a <see cref="RoutingException"/> is thrown on
    /// any unmatched response, forcing the caller to handle ambiguous routing explicitly.
    /// </param>
    public CapabilityRouter(
        IChatClient chatClient,
        IEnumerable<AgentDefinition> agents,
        ILogger<CapabilityRouter>? logger = null,
        string? fallbackAgentName = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(agents);

        _chatClient = chatClient;
        _agents = agents.ToList().AsReadOnly();
        _logger = logger ?? NullLogger<CapabilityRouter>.Instance;
        _fallbackAgentName = fallbackAgentName;

        if (_agents.Count == 0)
            throw new ArgumentException("At least one agent must be provided.", nameof(agents));

        if (fallbackAgentName is not null &&
            !_agents.Any(a => string.Equals(a.Name, fallbackAgentName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"Fallback agent '{fallbackAgentName}' is not among the registered agents.",
                nameof(fallbackAgentName));
        }
    }

    /// <summary>
    /// Selects the best agent for <paramref name="taskDescription"/> using a single LLM call.
    /// </summary>
    /// <exception cref="RoutingException">
    /// Thrown when the LLM response cannot be matched to any registered agent and no
    /// <c>fallbackAgentName</c> was supplied to the constructor.
    /// </exception>
    public async ValueTask<CapabilityRouterResult> RouteAsync(
        string taskDescription,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskDescription);

        // Fast path: only one candidate.
        if (_agents.Count == 1)
        {
            LogSingleAgentSkipping(_logger);
            return new CapabilityRouterResult(_agents[0], null);
        }

        var prompt = BuildPrompt(taskDescription);

        LogRoutingViaLlm(_logger, _agents.Count);

        ChatResponse response = await _chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            cancellationToken: cancellationToken);

        var rawResponse = response.Text?.Trim() ?? string.Empty;

        // Extract the agent name from the response (first line, trimmed).
        // Strip trailing punctuation that the LLM may append (e.g. "AgentA." or "AgentA:").
        var responseLine = (rawResponse.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                       .FirstOrDefault()
                                       ?.Trim() ?? string.Empty)
                           .TrimEnd('.', ':', ',', '!', '?');

        var selected = _agents.FirstOrDefault(
            a => string.Equals(a.Name, responseLine, StringComparison.OrdinalIgnoreCase));

        if (selected is null)
        {
            if (_fallbackAgentName is not null)
            {
                selected = _agents.First(
                    a => string.Equals(a.Name, _fallbackAgentName, StringComparison.OrdinalIgnoreCase));

                LogUnmatchedUsingFallback(_logger, responseLine, selected.Name);
            }
            else
            {
                LogUnmatchedNoFallback(_logger, responseLine);

                throw new RoutingException(responseLine);
            }
        }
        else
        {
            LogAgentSelected(_logger, selected.Name);
        }

        // Capture any reasoning the LLM returned beyond the first line.
        var reasoning = rawResponse.Contains('\n')
            ? rawResponse[(rawResponse.IndexOf('\n') + 1)..].Trim()
            : null;

        return new CapabilityRouterResult(selected, reasoning is { Length: > 0 } ? reasoning : null);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private string BuildPrompt(string taskDescription)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are a routing agent. Given a task, select the single best agent to handle it.");
        sb.AppendLine();
        sb.AppendLine("Available agents:");

        for (var i = 0; i < _agents.Count; i++)
        {
            var agent = _agents[i];
            sb.Append(i + 1).Append(". ").AppendLine(agent.Name);

            if (!string.IsNullOrWhiteSpace(agent.Description))
                sb.Append("   Description: ").AppendLine(agent.Description);

            if (agent.Capabilities is { Count: > 0 })
            {
                var caps = string.Join(", ", agent.Capabilities.Select(c => c.Id));
                sb.Append("   Capabilities: ").AppendLine(caps);
            }
        }

        sb.AppendLine();
        sb.Append("Task: ").AppendLine(taskDescription);
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY the agent name, exactly as listed above (first line). Do not add punctuation or explanation on that line.");

        return sb.ToString();
    }
}
