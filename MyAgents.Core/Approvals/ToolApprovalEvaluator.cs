using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace MyAgents.Core;

/// <summary>
/// Pipeline orchestrator that evaluates tool approvals against session memory, persistent stores,
/// policies, and interactive gates, producing audit and trace telemetry.
/// </summary>
public static class ToolApprovalEvaluator
{
    /// <summary>
    /// Computes a stable, deterministic approval ID for a given session, tool name, arguments JSON, and optional call ID.
    /// </summary>
    public static string CreateApprovalId(string sessionId, string toolName, string argumentsJson, string? callId = null)
    {
        if (!string.IsNullOrWhiteSpace(callId))
            return callId;

        var raw = $"{sessionId}\n{toolName}\n{argumentsJson}";
        return "approval-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..24];
    }

    /// <summary>
    /// Evaluates a tool approval request through the full policy gate pipeline using agent turn services.
    /// </summary>
    public static ValueTask<ToolApprovalDecision> EvaluateAsync(
        ToolApprovalRequest request,
        AgentTurnServices services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        return EvaluateAsync(
            request,
            services.GetService<ISessionApprovalMemory>(),
            services.GetService<IToolApprovalStore>(),
            services.GetService<IToolApprovalPolicy>(),
            services.GetService<IToolApprovalGate>(),
            services.GetService<IToolApprovalAuditSink>(),
            cancellationToken);
    }

    /// <summary>
    /// Evaluates a tool approval request through the full policy gate pipeline using an <see cref="IServiceProvider"/>.
    /// </summary>
    public static ValueTask<ToolApprovalDecision> EvaluateAsync(
        ToolApprovalRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        return EvaluateAsync(
            request,
            services.GetService<ISessionApprovalMemory>(),
            services.GetService<IToolApprovalStore>(),
            services.GetService<IToolApprovalPolicy>(),
            services.GetService<IToolApprovalGate>(),
            services.GetService<IToolApprovalAuditSink>(),
            cancellationToken);
    }

    /// <summary>
    /// Evaluates a tool approval request with explicit pipeline components.
    /// </summary>
    public static async ValueTask<ToolApprovalDecision> EvaluateAsync(
        ToolApprovalRequest request,
        ISessionApprovalMemory? sessionMemory,
        IToolApprovalStore? approvalStore,
        IToolApprovalPolicy? policy,
        IToolApprovalGate? gate,
        IToolApprovalAuditSink? auditSink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();

        // 1. Check Session Approval Memory (e.g. "always allow in session")
        if (sessionMemory is not null)
        {
            var granted = await sessionMemory.TryGetGrantedDecisionAsync(request, cancellationToken).ConfigureAwait(false);
            if (granted is not null)
            {
                await RecordAuditAsync(auditSink, request, granted, stopwatch.Elapsed, "SessionApprovalMemory", cancellationToken).ConfigureAwait(false);
                return granted;
            }
        }

        // 2. Check Persistent IToolApprovalStore
        if (approvalStore is not null)
        {
            var stored = await approvalStore.GetAsync(request.ApprovalId, cancellationToken).ConfigureAwait(false);
            if (stored is null && string.IsNullOrWhiteSpace(request.CallId))
            {
                var sessionApprovals = await approvalStore.ListBySessionAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
                stored = sessionApprovals.LastOrDefault(item =>
                    string.Equals(item.Request.ToolName, request.ToolName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Request.ArgumentsJson, request.ArgumentsJson, StringComparison.Ordinal));
            }

            if (stored?.Decision is { Status: not ToolApprovalStatus.Pending })
            {
                await RecordAuditAsync(auditSink, request, stored.Decision, stopwatch.Elapsed, "ToolApprovalStore", cancellationToken).ConfigureAwait(false);
                return stored.Decision;
            }
        }

        // 3. Evaluate Policy
        var decision = policy is null
            ? ToolApprovalDecision.Approve("default-allow")
            : await policy.DecideAsync(request, cancellationToken).ConfigureAwait(false);

        string? gateTypeName = null;

        // 4. If Pending, consult Interactive IToolApprovalGate
        if (decision.Status == ToolApprovalStatus.Pending && gate is not null)
        {
            gateTypeName = gate.GetType().Name;
            decision = await gate.EvaluateOrPromptAsync(request, decision, cancellationToken).ConfigureAwait(false);
        }

        // 5. Update Session Approval Memory if Approved with session scope
        if (decision.Status == ToolApprovalStatus.Approved &&
            decision.Scope != ApprovalScope.Once &&
            sessionMemory is not null)
        {
            await sessionMemory.RecordDecisionAsync(request, decision, cancellationToken).ConfigureAwait(false);
        }

        // 6. Update Persistent Store
        if (approvalStore is not null)
        {
            await approvalStore.SaveAsync(new PendingToolApproval(request.ApprovalId, request, decision), cancellationToken).ConfigureAwait(false);
        }

        // 7. Record Audit Sink
        await RecordAuditAsync(auditSink, request, decision, stopwatch.Elapsed, gateTypeName ?? policy?.GetType().Name, cancellationToken).ConfigureAwait(false);

        return decision;
    }

    private static async ValueTask RecordAuditAsync(
        IToolApprovalAuditSink? auditSink,
        ToolApprovalRequest request,
        ToolApprovalDecision decision,
        TimeSpan duration,
        string? gateType,
        CancellationToken cancellationToken)
    {
        if (auditSink is null)
            return;

        var auditRecord = new ToolApprovalAuditRecord(
            AuditId: Guid.NewGuid().ToString("N"),
            ApprovalId: request.ApprovalId,
            SessionId: request.SessionId,
            ToolName: request.ToolName,
            ArgumentsJson: request.ArgumentsJson,
            Decision: decision,
            Timestamp: DateTimeOffset.UtcNow,
            Duration: duration,
            GateType: gateType);

        await auditSink.RecordAsync(auditRecord, cancellationToken).ConfigureAwait(false);
    }
}