using Microsoft.Extensions.AI;
using MyAgents.Core;

namespace MyAgents.Agents;

/// <summary>
/// Configuration for <see cref="SelectorGroupChat"/>.
/// </summary>
public sealed record SelectorGroupChatOptions(
    /// <summary>
    /// The agents participating in the group chat.
    /// </summary>
    IReadOnlyList<AgentDefinition> Agents,

    /// <summary>
    /// The chat client used to select the next speaker.
    /// </summary>
    IChatClient SelectorClient,

    /// <summary>
    /// Optional system prompt for the selector LLM.
    /// Defaults to a standard prompt describing available agents.
    /// </summary>
    string? SelectorPrompt = null,

    /// <summary>
    /// Maximum number of turns before the group chat terminates.
    /// Defaults to 10.
    /// </summary>
    int MaxTurns = 10,

    /// <summary>
    /// Optional termination condition checked after each turn.
    /// </summary>
    ITerminationCondition? Termination = null);

/// <summary>
/// A group chat where an LLM selects which agent should speak next based on
/// the conversation context. This is the AutoGen <c>SelectorGroupChat</c> pattern.
/// </summary>
/// <remarks>
/// Unlike <see cref="GroupChat"/> (round-robin), this pattern uses an LLM to dynamically
/// determine the best agent for each turn based on the conversation so far.
/// </remarks>
public sealed class SelectorGroupChat
{
    private readonly SelectorGroupChatOptions _options;
    private readonly IAgentSessionFactory _factory;

    public SelectorGroupChat(SelectorGroupChatOptions options, IAgentSessionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options.Agents);
        ArgumentNullException.ThrowIfNull(options.SelectorClient);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Agents.Count, 0);

        _options = options;
        _factory = factory;
    }

    /// <summary>
    /// Runs the selector group chat until termination condition is met or max turns reached.
    /// </summary>
    /// <param name="initialMessages">Initial messages to start the conversation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The final conversation history, the last agent to speak, and selection reasoning.</returns>
    public async ValueTask<SelectorGroupChatResult> RunAsync(
        IReadOnlyList<AgentMessage> initialMessages,
        CancellationToken cancellationToken = default)
    {
        var conversation = new List<AgentMessage>(initialMessages);
        var turn = 0;
        var selectionReasoning = new List<string>();
        string? lastSpeaker = null;

        while (turn < _options.MaxTurns)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Select next agent via LLM
            var (agent, reasoning) = await SelectNextAgentAsync(conversation, turn, cancellationToken);

            selectionReasoning.Add($"Turn {turn + 1}: {agent.Name} - {reasoning}");

            // Create session for selected agent and invoke
            using var session = _factory.CreateSession($"selectorgroup-{agent.Name}-{Guid.NewGuid():N}", agent);

            var result = await session.InvokeAsync(conversation, cancellationToken);

            // Add agent's response to conversation
            conversation.AddRange(result.Output);
            lastSpeaker = agent.Name;
            turn++;

            // Check termination condition
            if (_options.Termination?.ShouldTerminate(result, turn) == true)
                break;
        }

        return new SelectorGroupChatResult(
            Conversation: conversation.AsReadOnly(),
            TotalTurns: turn,
            LastSpeaker: lastSpeaker ?? _options.Agents[0].Name,
            SelectionReasoning: selectionReasoning.AsReadOnly());
    }

    private async ValueTask<(AgentDefinition Agent, string Reasoning)> SelectNextAgentAsync(
        IReadOnlyList<AgentMessage> conversation,
        int turn,
        CancellationToken cancellationToken)
    {
        var prompt = BuildSelectorPrompt(conversation);

        ChatResponse response = await _options.SelectorClient.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, _options.SelectorPrompt ?? GetDefaultSelectorPrompt()),
                new ChatMessage(ChatRole.User, prompt)
            ],
            cancellationToken: cancellationToken);

        var rawResponse = response.Text?.Trim() ?? string.Empty;

        // Parse agent name from response
        var selectedName = ParseAgentName(rawResponse);
        var agent = _options.Agents.FirstOrDefault(
            a => string.Equals(a.Name, selectedName, StringComparison.OrdinalIgnoreCase))
            ?? _options.Agents[0];  // Fallback to first agent

        return (agent, rawResponse);
    }

    private string BuildSelectorPrompt(IReadOnlyList<AgentMessage> conversation)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Available agents:");

        for (var i = 0; i < _options.Agents.Count; i++)
        {
            var agent = _options.Agents[i];
            sb.AppendLine($"- {agent.Name}: {agent.Description ?? "No description"}");

            if (agent.Capabilities is { Count: > 0 })
            {
                sb.AppendLine($"  Capabilities: {string.Join(", ", agent.Capabilities.Select(c => c.Id))}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Conversation so far:");
        foreach (var msg in conversation.TakeLast(6))  // Last 6 messages to keep context manageable
        {
            sb.AppendLine($"[{msg.Role}]: {msg.Content[..Math.Min(msg.Content.Length, 200)]}");
        }

        sb.AppendLine();
        sb.AppendLine("Which agent should speak next? Respond with ONLY the agent name, exactly as listed above.");

        return sb.ToString();
    }

    private static string GetDefaultSelectorPrompt()
        => "You are a conversation manager. Select the best agent to respond next based on the conversation context.";

    private static string ParseAgentName(string response)
    {
        // Extract first line, trim, remove any punctuation
        var line = response.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                          .FirstOrDefault()
                          ?.Trim() ?? string.Empty;

        // Remove trailing punctuation that the LLM might add
        return line.TrimEnd('.', ':', ',', '!');
    }
}

/// <summary>
/// Result of a <see cref="SelectorGroupChat"/> run.
/// </summary>
public sealed record SelectorGroupChatResult(
    IReadOnlyList<AgentMessage> Conversation,
    int TotalTurns,
    string LastSpeaker,
    IReadOnlyList<string> SelectionReasoning);
