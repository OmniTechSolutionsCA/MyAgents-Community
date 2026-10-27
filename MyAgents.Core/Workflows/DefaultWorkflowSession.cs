using System.Text.Json;

namespace MyAgents.Core.Workflows;

/// <summary>
/// Concrete workflow session. Resolves agent calls by name from the registered agent dictionary,
/// creates per-call IAgentSessions, and manages typed workflow state.
/// </summary>
internal sealed class DefaultWorkflowSession<TState> : IWorkflowSession<TState>, IWorkflowApprovalContext, IDisposable
{
    private readonly IReadOnlyDictionary<string, AgentDefinition> _agents;
    private readonly IAgentSessionFactory _sessionFactory;
    private readonly string _workflowId;
    private readonly int _stateVersion;
    private readonly JsonSerializerOptions _jsonOptions;

    private readonly HumanInputRequest? _pendingHumanInput;
    private readonly PendingToolApproval? _pendingToolApproval;

    public string SessionId { get; }
    public TState State { get; set; }

    public DefaultWorkflowSession(
        string sessionId,
        TState initialState,
        IReadOnlyDictionary<string, AgentDefinition> agents,
        IAgentSessionFactory sessionFactory,
        string workflowId,
        int stateVersion,
        JsonSerializerOptions? jsonOptions = null,
        HumanInputRequest? pendingHumanInput = null,
        PendingToolApproval? pendingToolApproval = null)
    {
        SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        State = initialState;
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _workflowId = workflowId;
        _stateVersion = stateVersion;
        _jsonOptions = jsonOptions ?? JsonSerializerOptions.Default;
        _pendingHumanInput = pendingHumanInput;
        _pendingToolApproval = pendingToolApproval;
    }

    /// <inheritdoc />
    public HumanInputRequest? GetPendingHumanInput() => _pendingHumanInput;

    /// <inheritdoc />
    public PendingToolApproval? GetPendingToolApproval() => _pendingToolApproval;

    /// <inheritdoc />
    public async ValueTask<AgentTurnResult> CallAgentAsync(
        string agentName,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(messages);

        if (!_agents.TryGetValue(agentName, out var definition))
            throw new InvalidOperationException(
                $"Agent '{agentName}' is not registered in this workflow host. " +
                $"Registered agents: {string.Join(", ", _agents.Keys)}");

        // Each workflow call gets its own session (stateless within workflow step)
        var agentSessionId = $"{SessionId}:{agentName}:{Guid.NewGuid():N}";
        await using var agentSession = _sessionFactory.CreateSession(agentSessionId, definition);
        return await agentSession.InvokeAsync(messages, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentTurnResult> CallAgentStreamingAsync(
        string agentName,
        IReadOnlyList<AgentMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(messages);

        if (!_agents.TryGetValue(agentName, out var definition))
            throw new InvalidOperationException(
                $"Agent '{agentName}' is not registered in this workflow host. " +
                $"Registered agents: {string.Join(", ", _agents.Keys)}");

        var agentSessionId = $"{SessionId}:{agentName}:{Guid.NewGuid():N}";
        await using var agentSession = _sessionFactory.CreateSession(agentSessionId, definition);

        await foreach (var chunk in agentSession.InvokeStreamingAsync(messages, cancellationToken)
            .ConfigureAwait(false))
            yield return chunk;
    }
    /// <inheritdoc />
    public WorkflowSnapshot CreateSnapshot()
        => new(
            WorkflowId: _workflowId,
            StateVersion: _stateVersion,
            SessionId: SessionId,
            StateJson: JsonSerializer.Serialize(State, _jsonOptions))
        {
            PendingToolApproval = _pendingToolApproval
        };

    public void Dispose()
    {
    }

}
