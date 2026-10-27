using MyAgents.Core;

namespace MyAgents.Agents;

/// <summary>
/// Configuration for <see cref="GroupChat"/>.
/// </summary>
public sealed record GroupChatOptions(
    /// <summary>
    /// The agents participating in the group chat.
    /// </summary>
    IReadOnlyList<AgentDefinition> Agents,

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
/// A group chat where all registered agents receive each message and take turns responding.
/// This is the simplest multi-agent pattern — round-robin or random selection among agents.
/// </summary>
/// <remarks>
/// Each turn, one agent produces a response. The response is added to the shared conversation
/// history and broadcast to all agents for the next turn.
/// </remarks>
public sealed class GroupChat
{
    private readonly GroupChatOptions _options;
    private readonly IAgentSessionFactory _factory;
    private readonly Random _random = new();

    public GroupChat(GroupChatOptions options, IAgentSessionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options.Agents);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Agents.Count, 0);

        _options = options;
        _factory = factory;
    }

    /// <summary>
    /// Runs the group chat until termination condition is met or max turns reached.
    /// </summary>
    /// <param name="initialMessages">Initial messages to start the conversation.</param>
    /// <param name="selectionFunc">
    /// Optional function to select the next speaker.
    /// If null, agents are selected round-robin.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The final conversation history and the last agent to speak.</returns>
    public async ValueTask<GroupChatResult> RunAsync(
        IReadOnlyList<AgentMessage> initialMessages,
        Func<IReadOnlyList<AgentMessage>, int, AgentDefinition>? selectionFunc = null,
        CancellationToken cancellationToken = default)
    {
        var conversation = new List<AgentMessage>(initialMessages);
        var agentIndex = 0;
        var turn = 0;
        string? lastSpeaker = null;

        while (turn < _options.MaxTurns)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Select next agent
            var agent = selectionFunc is not null
                ? selectionFunc(conversation, turn)
                : _options.Agents[agentIndex % _options.Agents.Count];

            // Create session for this agent and invoke
            using var session = _factory.CreateSession($"groupchat-{agent.Name}-{Guid.NewGuid():N}", agent);

            var result = await session.InvokeAsync(conversation, cancellationToken);

            // Add agent's response to conversation
            conversation.AddRange(result.Output);
            lastSpeaker = agent.Name;
            agentIndex++;
            turn++;

            // Check termination condition
            if (_options.Termination?.ShouldTerminate(result, turn) == true)
                break;
        }

        return new GroupChatResult(
            Conversation: conversation.AsReadOnly(),
            TotalTurns: turn,
            LastSpeaker: lastSpeaker ?? _options.Agents[Math.Max(agentIndex - 1, 0) % _options.Agents.Count].Name);
    }
}

/// <summary>
/// Result of a <see cref="GroupChat"/> run.
/// </summary>
public sealed record GroupChatResult(
    IReadOnlyList<AgentMessage> Conversation,
    int TotalTurns,
    string LastSpeaker);
