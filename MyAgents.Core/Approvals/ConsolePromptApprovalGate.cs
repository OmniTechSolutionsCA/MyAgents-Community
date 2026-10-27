namespace MyAgents.Core;

/// <summary>
/// Prompts the operator via standard console input/output for interactive tool approval decisions.
/// </summary>
public sealed class ConsolePromptApprovalGate : IToolApprovalGate
{
    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly bool _autoApproveNonInteractive;

    public ConsolePromptApprovalGate(
        TextReader? reader = null,
        TextWriter? writer = null,
        bool autoApproveNonInteractive = false)
    {
        _reader = reader ?? Console.In;
        _writer = writer ?? Console.Out;
        _autoApproveNonInteractive = autoApproveNonInteractive;
    }

    public ValueTask<ToolApprovalDecision> EvaluateOrPromptAsync(
        ToolApprovalRequest request,
        ToolApprovalDecision pendingDecision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pendingDecision);
        cancellationToken.ThrowIfCancellationRequested();

        _writer.WriteLine();
        _writer.WriteLine($"[TOOL APPROVAL REQUIRED] Tool: '{request.ToolName}' in session '{request.SessionId}'");
        if (!string.IsNullOrWhiteSpace(pendingDecision.Reason))
        {
            _writer.WriteLine($"  Reason: {pendingDecision.Reason}");
        }
        _writer.WriteLine($"  Arguments: {request.ArgumentsJson}");
        _writer.Write("Action: [y]es (once) | [a]lways for this tool | [c]all matching args | [n]o (reject) | [s]kip (defer) > ");
        _writer.Flush();

        var input = _reader.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            return ValueTask.FromResult(_autoApproveNonInteractive
                ? ToolApprovalDecision.Approve("console-operator-noninteractive")
                : pendingDecision);
        }

        switch (input.ToLowerInvariant())
        {
            case "y":
            case "yes":
                return ValueTask.FromResult(ToolApprovalDecision.Approve("console-operator", "Approved by operator for this call", ApprovalScope.Once));

            case "a":
            case "always":
                return ValueTask.FromResult(ToolApprovalDecision.Approve("console-operator", "Approved by operator for all calls to this tool in session", ApprovalScope.SessionTool));

            case "c":
            case "call":
                return ValueTask.FromResult(ToolApprovalDecision.Approve("console-operator", "Approved by operator for matching arguments in session", ApprovalScope.SessionCall));

            case "n":
            case "no":
                return ValueTask.FromResult(ToolApprovalDecision.Reject("console-operator", "Rejected by operator"));

            case "s":
            case "skip":
            default:
                return ValueTask.FromResult(pendingDecision);
        }
    }
}