using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MyAgents.Core;
using MyAgents.Core.Execution;
using MyAgents.Core.Workflows;

namespace MyAgents.Runtime.Workers;

/// <summary>
/// Adapts a typed <see cref="IWorkflow{TState}"/> to the queue worker contract, including
/// human-input and tool-approval pauses.
/// </summary>
/// <remarks>
/// <para>
/// This adapter persists workflow snapshots when the workflow pauses for human input or tool
/// approval. For checkpoints between those pauses, workflow handlers should call
/// <see cref="AgentRunContext.SaveCheckpointAsync"/> with a handler-defined resumable payload.
/// </para>
/// <para>Serialization metadata is supplied explicitly for trimming and NativeAOT-friendly use.</para>
/// </remarks>
public sealed class WorkflowAgentRunHandler<TState> : IAgentRunHandler
{
    private readonly WorkflowHost _workflowHost;
    private readonly IWorkflow<TState> _workflow;
    private readonly JsonTypeInfo<TState> _stateTypeInfo;
    private readonly JsonTypeInfo<WorkflowSnapshot> _snapshotTypeInfo;
    private readonly JsonTypeInfo<ToolApprovalDecision> _approvalDecisionTypeInfo;

    /// <inheritdoc />
    public string HandlerId { get; }

    /// <summary>Creates a typed workflow handler with explicit JSON metadata.</summary>
    public WorkflowAgentRunHandler(
        string handlerId,
        WorkflowHost workflowHost,
        IWorkflow<TState> workflow,
        JsonTypeInfo<TState> stateTypeInfo,
        JsonTypeInfo<WorkflowSnapshot> snapshotTypeInfo,
        JsonTypeInfo<ToolApprovalDecision> approvalDecisionTypeInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerId);
        HandlerId = handlerId;
        _workflowHost = workflowHost ?? throw new ArgumentNullException(nameof(workflowHost));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _stateTypeInfo = stateTypeInfo ?? throw new ArgumentNullException(nameof(stateTypeInfo));
        _snapshotTypeInfo = snapshotTypeInfo ?? throw new ArgumentNullException(nameof(snapshotTypeInfo));
        _approvalDecisionTypeInfo = approvalDecisionTypeInfo ?? throw new ArgumentNullException(nameof(approvalDecisionTypeInfo));
    }

    /// <inheritdoc />
    public async ValueTask<AgentRunHandlerResult> ExecuteAsync(
        AgentRunContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var state = context.CheckpointJson is null
                ? await StartAsync(context, cancellationToken).ConfigureAwait(false)
                : await ResumeAsync(context, cancellationToken).ConfigureAwait(false);

            return AgentRunHandlerResult.Completed(JsonSerializer.Serialize(state, _stateTypeInfo));
        }
        catch (HumanInputRequiredException exception)
        {
            var snapshotJson = JsonSerializer.Serialize(exception.Snapshot, _snapshotTypeInfo);
            var description = exception.Snapshot.PendingHumanInput?.Prompt
                ?? exception.Snapshot.PendingToolApproval?.Request.Reason
                ?? exception.Message;

            return AgentRunHandlerResult.WaitingForInput(snapshotJson, description);
        }
    }

    private async ValueTask<TState> StartAsync(
        AgentRunContext context,
        CancellationToken cancellationToken)
    {
        var initialState = JsonSerializer.Deserialize(context.InputJson, _stateTypeInfo)
            ?? throw new InvalidOperationException("The run input could not be deserialized to workflow state.");

        return await _workflowHost.RunAsync(_workflow, initialState, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TState> ResumeAsync(
        AgentRunContext context,
        CancellationToken cancellationToken)
    {
        var snapshot = JsonSerializer.Deserialize(context.CheckpointJson!, _snapshotTypeInfo)
            ?? throw new InvalidOperationException("The run checkpoint could not be deserialized to a workflow snapshot.");

        if (snapshot.PendingHumanInput is not null)
        {
            var reply = context.ResumeInputJson is null
                ? throw new InvalidOperationException("A human-input reply is required to resume this workflow.")
                : JsonSerializer.Deserialize<string>(context.ResumeInputJson);
            ArgumentException.ThrowIfNullOrWhiteSpace(reply);
            return await _workflowHost.ResumeWithInputAsync(
                _workflow,
                snapshot,
                reply,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (snapshot.PendingToolApproval is not null)
        {
            var decision = context.ResumeInputJson is null
                ? throw new InvalidOperationException("A tool-approval decision is required to resume this workflow.")
                : JsonSerializer.Deserialize(context.ResumeInputJson, _approvalDecisionTypeInfo)
                    ?? throw new InvalidOperationException("The approval decision could not be deserialized.");
            return await _workflowHost.ResumeWithToolApprovalAsync(
                _workflow,
                snapshot,
                decision,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return await _workflowHost.ResumeAsync(_workflow, snapshot, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
