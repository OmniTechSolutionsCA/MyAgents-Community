using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MyAgents.Core;
using MyAgents.Core.Execution;

namespace MyAgents.Runtime.Workers;

/// <summary>
/// Checkpoint state persisted when a conversational <see cref="IAgentSession"/> run
/// pauses for asynchronous human-in-the-loop tool approval.
/// </summary>
public sealed record ChatAgentRunCheckpoint(
    IReadOnlyList<AgentMessage> Messages,
    PendingToolApproval PendingToolApproval);

/// <summary>
/// Adapts a conversational <see cref="AgentDefinition"/> and <see cref="IAgentSessionFactory"/>
/// to the durable <see cref="IAgentRunHandler"/> contract, supporting asynchronous
/// suspend/resume on <see cref="ToolApprovalRequiredException"/>.
/// </summary>
public sealed class ChatAgentRunHandler : IAgentRunHandler
{
    private readonly IAgentSessionFactory _sessionFactory;
    private readonly AgentDefinition _definition;
    private readonly JsonTypeInfo<IReadOnlyList<AgentMessage>> _messagesTypeInfo;
    private readonly JsonTypeInfo<ChatAgentRunCheckpoint> _checkpointTypeInfo;
    private readonly JsonTypeInfo<ToolApprovalDecision> _approvalDecisionTypeInfo;
    private readonly JsonTypeInfo<AgentTurnResult> _turnResultTypeInfo;

    /// <inheritdoc />
    public string HandlerId { get; }

    /// <summary>Creates a conversational agent run handler with explicit NativeAOT-safe JSON metadata.</summary>
    public ChatAgentRunHandler(
        string handlerId,
        IAgentSessionFactory sessionFactory,
        AgentDefinition definition,
        JsonTypeInfo<IReadOnlyList<AgentMessage>> messagesTypeInfo,
        JsonTypeInfo<ChatAgentRunCheckpoint> checkpointTypeInfo,
        JsonTypeInfo<ToolApprovalDecision> approvalDecisionTypeInfo,
        JsonTypeInfo<AgentTurnResult> turnResultTypeInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerId);
        HandlerId = handlerId;
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _messagesTypeInfo = messagesTypeInfo ?? throw new ArgumentNullException(nameof(messagesTypeInfo));
        _checkpointTypeInfo = checkpointTypeInfo ?? throw new ArgumentNullException(nameof(checkpointTypeInfo));
        _approvalDecisionTypeInfo = approvalDecisionTypeInfo ?? throw new ArgumentNullException(nameof(approvalDecisionTypeInfo));
        _turnResultTypeInfo = turnResultTypeInfo ?? throw new ArgumentNullException(nameof(turnResultTypeInfo));
    }

    /// <inheritdoc />
    public async ValueTask<AgentRunHandlerResult> ExecuteAsync(
        AgentRunContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<AgentMessage> messages;
        if (context.CheckpointJson is null)
        {
            messages = JsonSerializer.Deserialize(context.InputJson, _messagesTypeInfo)
                ?? throw new InvalidOperationException("The run input could not be deserialized to agent messages.");
        }
        else
        {
            var checkpoint = JsonSerializer.Deserialize(context.CheckpointJson, _checkpointTypeInfo)
                ?? throw new InvalidOperationException("The run checkpoint could not be deserialized.");

            var decision = context.ResumeInputJson is null
                ? throw new InvalidOperationException("A tool-approval decision is required to resume this chat run.")
                : JsonSerializer.Deserialize(context.ResumeInputJson, _approvalDecisionTypeInfo)
                    ?? throw new InvalidOperationException("The approval decision could not be deserialized.");

            var pending = checkpoint.PendingToolApproval;
            var effectiveArgs = decision.ModifiedArgumentsJson ?? pending.Request.ArgumentsJson;
            var resumeNotice = decision.Status == ToolApprovalStatus.Approved
                ? $"[Approval granted by {decision.DecidedBy ?? "reviewer"} for tool '{pending.Request.ToolName}' with arguments: {effectiveArgs}]"
                : $"[Approval rejected by {decision.DecidedBy ?? "reviewer"} for tool '{pending.Request.ToolName}'. Reason: {decision.Reason ?? "Not approved."}]";

            messages = [.. checkpoint.Messages, AgentMessage.FromText("system", resumeNotice)];
        }

        await using var session = _sessionFactory.CreateSession(context.RunId, _definition);
        try
        {
            var result = await session.InvokeAsync(messages, cancellationToken).ConfigureAwait(false);
            return AgentRunHandlerResult.Completed(JsonSerializer.Serialize(result, _turnResultTypeInfo));
        }
        catch (ToolApprovalRequiredException exception)
        {
            var checkpoint = new ChatAgentRunCheckpoint(messages, exception.Approval);
            var checkpointJson = JsonSerializer.Serialize(checkpoint, _checkpointTypeInfo);
            var description = exception.Approval.Request.Reason ?? exception.Message;
            return AgentRunHandlerResult.WaitingForInput(checkpointJson, description);
        }
    }
}
