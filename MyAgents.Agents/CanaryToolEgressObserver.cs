using MyAgents.Core;
using MyAgents.Core.Security;
using MyAgents.Core.Tools;

namespace MyAgents.Agents;

/// <summary>
/// Scans a tool call's arguments before the tool is invoked, and aborts the call on a violation.
/// </summary>
/// <remarks>
/// <para>
/// This is the highest-value interception point in the framework: a web-search query, an HTTP call,
/// an MCP request or a subagent prompt is the actual channel by which data leaves the process. The
/// observer runs before <c>InvokeAsync</c>, so a violation means the network call never happens.
/// </para>
/// <para>
/// Arguments cannot be rewritten — <see cref="IToolCallObserver"/> returns no value and the runtime
/// passes the original argument string to the tool. The only outcomes are abort or allow; redaction
/// is not available on this channel.
/// </para>
/// </remarks>
internal sealed class CanaryToolEgressObserver : IToolCallObserver
{
    private readonly ICanaryVault _vault;
    private readonly IEgressScanner? _scanner;
    private readonly ICanaryViolationHandler _handler;
    private readonly CanaryDefenseOptions _options;

    public CanaryToolEgressObserver(
        ICanaryVault vault,
        IEgressScanner? scanner,
        ICanaryViolationHandler handler,
        CanaryDefenseOptions options)
    {
        _vault = vault;
        _scanner = scanner;
        _handler = handler;
        _options = options;
    }

    public async ValueTask OnToolCallingAsync(
        ToolCallObservation call,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (!_options.ScanToolArguments)
            return;

        if (_vault.IsQuarantined)
            throw new CanaryViolationException("Session is quarantined; tool calls are refused.");

        var scan = await EgressGuard
            .ScanAsync(
                _vault.Canaries,
                _scanner,
                EgressChannel.ToolArguments,
                call.Arguments ?? string.Empty,
                _options.MaxScanBytes,
                _options.FailOpenOnScannerError,
                cancellationToken)
            .ConfigureAwait(false);

        if (scan.Severity == EgressSeverity.Informational)
            return;

        await CanaryEgressMiddleware.RecordAuditAsync(
            _options.AuditSink,
            call.SessionId,
            EgressChannel.ToolArguments,
            scan.Findings,
            toolName: call.ToolName,
            cancellationToken).ConfigureAwait(false);

        var disposition = await _handler
            .HandleAsync(
                new CanaryViolationContext(EgressChannel.ToolArguments, scan, call.SessionId),
                cancellationToken)
            .ConfigureAwait(false);

        if (disposition == CanaryDisposition.Abort)
        {
            throw new CanaryViolationException(
                $"Tool '{call.ToolName}' was refused by egress defense on the {scan.Severity} channel.");
        }
    }
}
