using System.Text.RegularExpressions;

namespace MyAgents.Core;

/// <summary>
/// A rule-based tool approval policy that evaluates tool names, argument patterns, and custom predicates.
/// </summary>
public sealed class RuleBasedToolApprovalPolicy : IToolApprovalPolicy
{
    private readonly List<Func<ToolApprovalRequest, ToolApprovalDecision?>> _customRules = [];
    private readonly List<ArgumentRule> _argumentRules = [];
    private readonly List<ToolNameRule> _toolNameRules = [];
    private ToolApprovalDecision _defaultDecision = ToolApprovalDecision.Pending("Approval required by default policy.");

    /// <summary>
    /// Sets the default decision when no explicit rules match.
    /// </summary>
    public RuleBasedToolApprovalPolicy SetDefaultDecision(ToolApprovalDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        _defaultDecision = decision;
        return this;
    }

    /// <summary>
    /// Configures tools matching the pattern to be auto-approved.
    /// </summary>
    public RuleBasedToolApprovalPolicy AllowTool(string toolPattern, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolPattern);
        _toolNameRules.Add(new ToolNameRule(toolPattern, ToolApprovalDecision.Approve("rule-policy", reason ?? $"Tool '{toolPattern}' is allow-listed.")));
        return this;
    }

    /// <summary>
    /// Configures tools matching the pattern to require human approval.
    /// </summary>
    public RuleBasedToolApprovalPolicy RequireApprovalForTool(string toolPattern, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolPattern);
        _toolNameRules.Add(new ToolNameRule(toolPattern, ToolApprovalDecision.Pending(reason ?? $"Tool '{toolPattern}' requires policy approval.")));
        return this;
    }

    /// <summary>
    /// Configures tools matching the pattern to be denied/rejected immediately.
    /// </summary>
    public RuleBasedToolApprovalPolicy DenyTool(string toolPattern, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolPattern);
        _toolNameRules.Add(new ToolNameRule(toolPattern, ToolApprovalDecision.Reject("rule-policy", reason ?? $"Tool '{toolPattern}' is blocked by policy.")));
        return this;
    }

    /// <summary>
    /// Adds a regex rule on the JSON arguments for tools matching the pattern.
    /// </summary>
    public RuleBasedToolApprovalPolicy AddArgumentPatternRule(
        string toolPattern,
        Regex argumentRegex,
        ToolApprovalDecision decision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolPattern);
        ArgumentNullException.ThrowIfNull(argumentRegex);
        ArgumentNullException.ThrowIfNull(decision);

        _argumentRules.Add(new ArgumentRule(toolPattern, argumentRegex, decision));
        return this;
    }

    /// <summary>
    /// Adds a custom evaluation predicate rule.
    /// </summary>
    public RuleBasedToolApprovalPolicy AddRule(Func<ToolApprovalRequest, ToolApprovalDecision?> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _customRules.Add(rule);
        return this;
    }

    public ValueTask<ToolApprovalDecision> DecideAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Custom predicate rules
        foreach (var rule in _customRules)
        {
            var decision = rule(request);
            if (decision is not null)
                return ValueTask.FromResult(decision);
        }

        // 2. Argument regex rules
        foreach (var argRule in _argumentRules)
        {
            if (MatchesPattern(request.ToolName, argRule.ToolPattern) &&
                argRule.ArgumentRegex.IsMatch(request.ArgumentsJson))
            {
                return ValueTask.FromResult(argRule.Decision);
            }
        }

        // 3. Tool name rules
        foreach (var toolRule in _toolNameRules)
        {
            if (MatchesPattern(request.ToolName, toolRule.Pattern))
            {
                return ValueTask.FromResult(toolRule.Decision);
            }
        }

        // 4. Default decision
        return ValueTask.FromResult(_defaultDecision);
    }

    private static bool MatchesPattern(string input, string pattern)
    {
        if (string.Equals(pattern, "*", StringComparison.Ordinal) ||
            string.Equals(pattern, input, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (pattern.EndsWith('*') && input.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase))
            return true;

        if (pattern.StartsWith('*') && input.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private sealed record ToolNameRule(string Pattern, ToolApprovalDecision Decision);

    private sealed record ArgumentRule(string ToolPattern, Regex ArgumentRegex, ToolApprovalDecision Decision);
}