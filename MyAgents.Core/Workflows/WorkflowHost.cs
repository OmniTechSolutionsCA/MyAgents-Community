namespace MyAgents.Core.Workflows;

/// <summary>
/// Hosts and executes IWorkflow&lt;TState&gt; instances with a named registry of agents.
/// Supports sequential, concurrent, and handoff execution patterns.
///
/// Usage:
///   var host = new WorkflowHost(sessionFactory);
///   host.RegisterAgent("researcher", researcherDefinition);
///   host.RegisterAgent("writer", writerDefinition);
///   await host.RunAsync(myWorkflow, initialState);
/// </summary>
public sealed class WorkflowHost
{
    private readonly IAgentSessionFactory _sessionFactory;
    private readonly Dictionary<string, AgentDefinition> _agents = new(StringComparer.OrdinalIgnoreCase);

    public WorkflowHost(IAgentSessionFactory sessionFactory)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    }

    /// <summary>
    /// Registers an agent definition under the given name so workflows can call it by name.
    /// </summary>
    public WorkflowHost RegisterAgent(string agentName, AgentDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(definition);
        _agents[agentName] = definition;
        return this;
    }

    /// <summary>
    /// Runs the given workflow with an initial state value.
    /// </summary>
    public async ValueTask<TState> RunAsync<TState>(
        IWorkflow<TState> workflow,
        TState initialState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var sessionId = $"{workflow.Id}:{Guid.NewGuid():N}";
        using var session = new DefaultWorkflowSession<TState>(
            sessionId,
            initialState,
            _agents,
            _sessionFactory,
            workflow.Id,
            workflow.StateVersion);

        await workflow.RunAsync(session, cancellationToken).ConfigureAwait(false);
        return session.State;
    }

    /// <summary>
    /// Restores and continues a workflow from a snapshot.
    /// </summary>
    public async ValueTask<TState> ResumeAsync<TState>(
        IWorkflow<TState> workflow,
        WorkflowSnapshot snapshot,
        System.Text.Json.JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.WorkflowId != workflow.Id)
            throw new ArgumentException(
                $"Snapshot workflow ID '{snapshot.WorkflowId}' does not match workflow ID '{workflow.Id}'.",
                nameof(snapshot));

        var opts = jsonOptions ?? System.Text.Json.JsonSerializerOptions.Default;
        var state = System.Text.Json.JsonSerializer.Deserialize<TState>(snapshot.StateJson, opts)
            ?? throw new InvalidOperationException("Failed to deserialize workflow state from snapshot.");

        using var session = new DefaultWorkflowSession<TState>(
            snapshot.SessionId,
            state,
            _agents,
            _sessionFactory,
            workflow.Id,
            workflow.StateVersion,
            opts,
            pendingHumanInput: snapshot.PendingHumanInput,
            pendingToolApproval: snapshot.PendingToolApproval);

        await workflow.RunAsync(session, cancellationToken).ConfigureAwait(false);
        return session.State;
    }

    /// <summary>
    /// Injects a human reply into a paused snapshot and resumes workflow execution.
    /// </summary>
    /// <typeparam name="TState">The workflow state type.</typeparam>
    /// <param name="workflow">The workflow to resume.</param>
    /// <param name="snapshot">
    ///     The snapshot captured from <see cref="HumanInputRequiredException.Snapshot"/>.
    ///     Must have a non-null <see cref="WorkflowSnapshot.PendingHumanInput"/>.
    /// </param>
    /// <param name="humanReply">The human's reply text.</param>
    /// <param name="jsonOptions">Optional JSON serialization options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The final workflow state after completion.</returns>
    public async ValueTask<TState> ResumeWithInputAsync<TState>(
        IWorkflow<TState> workflow,
        WorkflowSnapshot snapshot,
        string humanReply,
        System.Text.Json.JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(humanReply);

        if (snapshot.PendingHumanInput is null)
            throw new ArgumentException(
                "Snapshot does not have a pending human-input request. " +
                "Use ResumeAsync for non-HITL snapshots.",
                nameof(snapshot));

        // Inject the human reply into the snapshot before resuming.
        var resumeSnapshot = snapshot with
        {
            PendingHumanInput = snapshot.PendingHumanInput with { Reply = humanReply }
        };

        return await ResumeAsync(workflow, resumeSnapshot, jsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Injects a tool-approval decision into a paused snapshot and resumes workflow execution.
    /// </summary>
    public async ValueTask<TState> ResumeWithToolApprovalAsync<TState>(
        IWorkflow<TState> workflow,
        WorkflowSnapshot snapshot,
        ToolApprovalDecision decision,
        System.Text.Json.JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(decision);

        if (snapshot.PendingToolApproval is null)
            throw new ArgumentException(
                "Snapshot does not have a pending tool-approval request.",
                nameof(snapshot));

        var resumeSnapshot = snapshot with
        {
            PendingToolApproval = snapshot.PendingToolApproval with
            {
                Decision = decision
            }
        };

        return await ResumeAsync(workflow, resumeSnapshot, jsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }
}
