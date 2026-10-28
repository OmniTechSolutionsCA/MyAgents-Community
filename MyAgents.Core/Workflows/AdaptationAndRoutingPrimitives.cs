using System.Globalization;
namespace MyAgents.Core.Workflows;

/// <summary>
/// High-level status for an observed workflow or agent outcome.
/// </summary>
public enum OutcomeStatus
{
    Succeeded = 0,
    PartiallySucceeded = 1,
    Blocked = 2,
    Failed = 3,
}

/// <summary>
/// Source category for structured feedback.
/// </summary>
public enum FeedbackKind
{
    Human = 0,
    Evaluation = 1,
    Tooling = 2,
    System = 3,
}

/// <summary>
/// Target area that an adaptation hint is intended to influence.
/// </summary>
public enum AdaptationHintKind
{
    Prompt = 0,
    Planner = 1,
    Goal = 2,
}

/// <summary>
/// Structured record of a completed or attempted execution outcome.
/// </summary>
public sealed record OutcomeRecord
{
    public OutcomeRecord(
        string name,
        OutcomeStatus status,
        string summary,
        string? goalId = null,
        double? score = null,
        TimeSpan? duration = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);

        Name = name;
        Status = status;
        Summary = summary;
        GoalId = goalId;
        Score = score;
        Duration = duration;
        Metadata = metadata is null
            ? null
            : new Dictionary<string, object?>(metadata, StringComparer.OrdinalIgnoreCase);
    }

    public string Name { get; init; }

    public OutcomeStatus Status { get; init; }

    public string Summary { get; init; }

    public string? GoalId { get; init; }

    public double? Score { get; init; }

    public TimeSpan? Duration { get; init; }

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>
/// Structured feedback input captured alongside execution outcomes.
/// </summary>
public sealed record FeedbackRecord
{
    public FeedbackRecord(
        FeedbackKind kind,
        string summary,
        double? score = null,
        string? goalId = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);

        Kind = kind;
        Summary = summary;
        Score = score;
        GoalId = goalId;
        Metadata = metadata is null
            ? null
            : new Dictionary<string, object?>(metadata, StringComparer.OrdinalIgnoreCase);
    }

    public FeedbackKind Kind { get; init; }

    public string Summary { get; init; }

    public double? Score { get; init; }

    public string? GoalId { get; init; }

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>
/// Bounded adaptation artifact returned by an adaptation policy.
/// </summary>
public sealed record AdaptationHint
{
    public AdaptationHint(
        AdaptationHintKind kind,
        string content,
        string? goalId = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        Kind = kind;
        Content = content;
        GoalId = goalId;
        Metadata = metadata is null
            ? null
            : new Dictionary<string, object?>(metadata, StringComparer.OrdinalIgnoreCase);
    }

    public AdaptationHintKind Kind { get; init; }

    public string Content { get; init; }

    public string? GoalId { get; init; }

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>
/// Adaptation evaluation context owned by the caller.
/// </summary>
public sealed record AdaptationContext(
    IReadOnlyList<OutcomeRecord> Outcomes,
    IReadOnlyList<FeedbackRecord> Feedback,
    GoalCollection? Goals = null,
    IReadOnlyDictionary<string, object?>? Metadata = null)
{
    public IReadOnlyList<OutcomeRecord> Outcomes { get; init; } = Outcomes ?? throw new ArgumentNullException(nameof(Outcomes));

    public IReadOnlyList<FeedbackRecord> Feedback { get; init; } = Feedback ?? throw new ArgumentNullException(nameof(Feedback));

    public GoalCollection? Goals { get; init; } = Goals;

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; } = Metadata;
}

/// <summary>
/// Result returned by an adaptation policy.
/// </summary>
public sealed record AdaptationResult(
    IReadOnlyList<AdaptationHint> Hints,
    string? Reason = null)
{
    public IReadOnlyList<AdaptationHint> Hints { get; init; } = Hints ?? throw new ArgumentNullException(nameof(Hints));

    public string? Reason { get; init; } = Reason;
}

/// <summary>
/// Policy that converts structured outcomes and feedback into bounded hints.
/// </summary>
public interface IAdaptationPolicy
{
    ValueTask<AdaptationResult> EvaluateAsync(
        AdaptationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default deterministic adaptation policy.
/// </summary>
public sealed class DefaultAdaptationPolicy : IAdaptationPolicy
{
    public DefaultAdaptationPolicy(double lowScoreThreshold = 0.5, int maxHints = 5)
    {
        if (double.IsNaN(lowScoreThreshold) || double.IsInfinity(lowScoreThreshold))
            throw new ArgumentOutOfRangeException(nameof(lowScoreThreshold));

        if (lowScoreThreshold < 0.0 || lowScoreThreshold > 1.0)
            throw new ArgumentOutOfRangeException(nameof(lowScoreThreshold));

        ArgumentOutOfRangeException.ThrowIfLessThan(maxHints, 1);

        LowScoreThreshold = lowScoreThreshold;
        MaxHints = maxHints;
    }

    public double LowScoreThreshold { get; }

    public int MaxHints { get; }

    public ValueTask<AdaptationResult> EvaluateAsync(
        AdaptationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var hints = new List<AdaptationHint>();
        var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var groupedNegativeOutcomes = context.Outcomes
            .Where(static outcome => outcome.Status is OutcomeStatus.Blocked or OutcomeStatus.Failed)
            .GroupBy(static outcome => $"{outcome.GoalId ?? string.Empty}|{outcome.Name}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var group in groupedNegativeOutcomes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = group.First();

            AddHint(
                hints,
                dedupe,
                new AdaptationHint(
                    AdaptationHintKind.Prompt,
                    $"Tighten execution instructions for '{sample.Name}' to address: {sample.Summary}",
                    sample.GoalId));

            if (group.Count() > 1 || group.Any(static outcome => ContainsSequencingSignal(outcome.Summary)))
            {
                AddHint(
                    hints,
                    dedupe,
                    new AdaptationHint(
                        AdaptationHintKind.Planner,
                        $"Revise planning around '{sample.Name}' to reduce repeated failure or blocked sequencing.",
                        sample.GoalId));
            }

            if (!string.IsNullOrWhiteSpace(sample.GoalId))
            {
                AddHint(
                    hints,
                    dedupe,
                    new AdaptationHint(
                        AdaptationHintKind.Goal,
                        $"Reassess goal '{sample.GoalId}' dependencies, urgency, or readiness before the next attempt.",
                        sample.GoalId));
            }
        }

        var lowScoreFeedback = context.Feedback
            .Where(feedback => feedback.Score is double score && score <= LowScoreThreshold)
            .OrderBy(static feedback => feedback.GoalId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static feedback => feedback.Kind)
            .ThenBy(static feedback => feedback.Summary, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var feedback in lowScoreFeedback)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kind = feedback.GoalId is not null
                ? AdaptationHintKind.Goal
                : feedback.Kind is FeedbackKind.Tooling or FeedbackKind.System
                    ? AdaptationHintKind.Planner
                    : AdaptationHintKind.Prompt;

            var prefix = kind switch
            {
                AdaptationHintKind.Goal => $"Update goal '{feedback.GoalId}' handling based on feedback",
                AdaptationHintKind.Planner => "Adjust planning policy based on feedback",
                _ => "Refine execution prompt based on feedback",
            };

            AddHint(
                hints,
                dedupe,
                new AdaptationHint(kind, $"{prefix}: {feedback.Summary}", feedback.GoalId));
        }

        var ordered = hints
            .OrderBy(static hint => hint.GoalId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static hint => hint.Kind)
            .ThenBy(static hint => hint.Content, StringComparer.OrdinalIgnoreCase)
            .Take(MaxHints)
            .ToArray();

        return ValueTask.FromResult(new AdaptationResult(
            ordered,
            ordered.Length == 0
                ? "No adaptation hints were generated."
                : $"Generated {ordered.Length} adaptation hint(s)."));
    }

    private static void AddHint(
        List<AdaptationHint> hints,
        HashSet<string> dedupe,
        AdaptationHint hint)
    {
        var key = $"{hint.GoalId ?? string.Empty}|{hint.Kind}";
        if (dedupe.Add(key))
            hints.Add(hint);
    }

    private static bool ContainsSequencingSignal(string summary)
        => summary.Contains("sequence", StringComparison.OrdinalIgnoreCase)
        || summary.Contains("dependency", StringComparison.OrdinalIgnoreCase)
        || summary.Contains("order", StringComparison.OrdinalIgnoreCase)
        || summary.Contains("plan", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Aggregates multiple adaptation policies into a single bounded result set.
/// </summary>
public sealed class CompositeAdaptationPolicy : IAdaptationPolicy
{
    private readonly IAdaptationPolicy[] _policies;

    public CompositeAdaptationPolicy(params IAdaptationPolicy[] policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        _policies = policies.ToArray();
        if (_policies.Length == 0)
            throw new ArgumentException("At least one adaptation policy must be provided.", nameof(policies));
    }

    public async ValueTask<AdaptationResult> EvaluateAsync(
        AdaptationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var hints = new List<AdaptationHint>();
        var reasons = new List<string>();
        var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var policy in _policies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await policy.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
            foreach (var hint in result.Hints)
            {
                var key = $"{hint.GoalId ?? string.Empty}|{hint.Kind}|{hint.Content}";
                if (dedupe.Add(key))
                    hints.Add(hint);
            }

            if (!string.IsNullOrWhiteSpace(result.Reason))
                reasons.Add(result.Reason);
        }

        return new AdaptationResult(
            hints
                .OrderBy(static hint => hint.GoalId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static hint => hint.Kind)
                .ThenBy(static hint => hint.Content, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            reasons.Count == 0 ? null : string.Join("; ", reasons));
    }
}

/// <summary>
/// Explicit helpers for recording adaptation inputs and evaluating hints.
/// </summary>
public static class AdaptationSteps
{
    public static OutcomeRecord RecordOutcome(
        ICollection<OutcomeRecord> outcomes,
        string name,
        OutcomeStatus status,
        string summary,
        string? goalId = null,
        double? score = null,
        TimeSpan? duration = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        var record = new OutcomeRecord(name, status, summary, goalId, score, duration, metadata);
        outcomes.Add(record);
        return record;
    }

    public static FeedbackRecord RecordFeedback(
        ICollection<FeedbackRecord> feedback,
        FeedbackKind kind,
        string summary,
        double? score = null,
        string? goalId = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(feedback);

        var record = new FeedbackRecord(kind, summary, score, goalId, metadata);
        feedback.Add(record);
        return record;
    }

    public static ValueTask<AdaptationResult> EvaluateAsync(
        IReadOnlyList<OutcomeRecord> outcomes,
        IReadOnlyList<FeedbackRecord> feedback,
        GoalCollection? goals = null,
        IAdaptationPolicy? policy = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var context = new AdaptationContext(outcomes, feedback, goals, metadata);
        return (policy ?? new DefaultAdaptationPolicy()).EvaluateAsync(context, cancellationToken);
    }

    public static IReadOnlyList<AgentMessage> BuildAdaptationMessages(
        IReadOnlyList<AdaptationHint> hints,
        string? additionalInstruction = null)
    {
        ArgumentNullException.ThrowIfNull(hints);

        var lines = new List<string>
        {
            "Adaptation hints:"
        };

        foreach (var hint in hints)
        {
            var target = string.IsNullOrWhiteSpace(hint.GoalId)
                ? string.Empty
                : $" [Goal: {hint.GoalId}]";
            lines.Add($"- {hint.Kind}{target}: {hint.Content}");
        }

        if (!string.IsNullOrWhiteSpace(additionalInstruction))
            lines.Add(additionalInstruction);

        return [new AgentMessage("user", string.Join(Environment.NewLine, lines))];
    }
}

/// <summary>
/// Simple capability requirement for route selection.
/// </summary>
public sealed record RoutingCapabilityRequirement
{
    public RoutingCapabilityRequirement(string capabilityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        CapabilityId = capabilityId;
    }

    public string CapabilityId { get; init; }
}

/// <summary>
/// Candidate route that can be selected for the next workflow step.
/// </summary>
public sealed record RoutingCandidate
{
    public RoutingCandidate(
        string candidateId,
        string target,
        double? estimatedCost = null,
        TimeSpan? estimatedLatency = null,
        int? maxInputTokens = null,
        IReadOnlyList<string>? capabilities = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        CandidateId = candidateId;
        Target = target;
        EstimatedCost = estimatedCost;
        EstimatedLatency = estimatedLatency;
        MaxInputTokens = maxInputTokens;
        Capabilities = capabilities?.ToArray() ?? Array.Empty<string>();
        Metadata = metadata is null
            ? null
            : new Dictionary<string, object?>(metadata, StringComparer.OrdinalIgnoreCase);
    }

    public string CandidateId { get; init; }

    public string Target { get; init; }

    public double? EstimatedCost { get; init; }

    public TimeSpan? EstimatedLatency { get; init; }

    public int? MaxInputTokens { get; init; }

    public IReadOnlyList<string> Capabilities { get; init; }

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>
/// Context passed into route selection policies.
/// </summary>
public sealed record RoutingContext(
    IReadOnlyList<RoutingCandidate> Candidates,
    int? EstimatedInputTokens = null,
    IReadOnlyList<RoutingCapabilityRequirement>? RequiredCapabilities = null,
    GoalRecord? Goal = null,
    IReadOnlyDictionary<string, object?>? Metadata = null)
{
    public IReadOnlyList<RoutingCandidate> Candidates { get; init; } = Candidates ?? throw new ArgumentNullException(nameof(Candidates));

    public int? EstimatedInputTokens { get; init; } = EstimatedInputTokens;

    public IReadOnlyList<RoutingCapabilityRequirement>? RequiredCapabilities { get; init; } = RequiredCapabilities;

    public GoalRecord? Goal { get; init; } = Goal;

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; } = Metadata;
}

/// <summary>
/// Result returned from route scoring.
/// </summary>
public sealed record RoutingScoreResult(
    double Score,
    string? Reason = null);

/// <summary>
/// Selected route decision.
/// </summary>
public sealed record RoutingDecision(
    RoutingCandidate Candidate,
    double Score,
    string? Reason = null)
{
    public RoutingCandidate Candidate { get; init; } = Candidate ?? throw new ArgumentNullException(nameof(Candidate));
}

/// <summary>
/// Scores routing candidates against a workflow context.
/// </summary>
public interface IModelRoutingPolicy
{
    ValueTask<RoutingScoreResult> ScoreAsync(
        RoutingCandidate candidate,
        RoutingContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default deterministic routing policy.
/// </summary>
public sealed class DefaultModelRoutingPolicy : IModelRoutingPolicy
{
    public ValueTask<RoutingScoreResult> ScoreAsync(
        RoutingCandidate candidate,
        RoutingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (context.RequiredCapabilities is { Count: > 0 })
        {
            var capabilities = candidate.Capabilities.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var requirement in context.RequiredCapabilities)
            {
                if (!capabilities.Contains(requirement.CapabilityId))
                {
                    return ValueTask.FromResult(new RoutingScoreResult(
                        double.NegativeInfinity,
                        $"Missing required capability '{requirement.CapabilityId}'."));
                }
            }
        }

        if (context.EstimatedInputTokens is int estimatedTokens
            && candidate.MaxInputTokens is int maxInputTokens
            && maxInputTokens < estimatedTokens)
        {
            return ValueTask.FromResult(new RoutingScoreResult(
                double.NegativeInfinity,
                $"Token budget {maxInputTokens} is below required input {estimatedTokens}."));
        }

        var latencyUnits = candidate.EstimatedLatency?.TotalMilliseconds ?? 1_000_000_000d;
        var costUnits = candidate.EstimatedCost ?? 1_000_000d;
        var headroom = candidate.MaxInputTokens.HasValue && context.EstimatedInputTokens.HasValue
            ? candidate.MaxInputTokens.Value - context.EstimatedInputTokens.Value
            : -1_000_000;

        var latencyWeight = context.Goal?.Definition.Priority is GoalPriority.High or GoalPriority.Critical
            ? 1_000_000_000d
            : 100_000_000d;

        var score =
            -(latencyUnits * latencyWeight)
            -(costUnits * 10_000d)
            + headroom;

        var reason = $"Latency={candidate.EstimatedLatency?.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown"}ms; " +
                     $"Cost={candidate.EstimatedCost?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown"}; " +
                     $"Headroom={(headroom <= -1_000_000 ? "unknown" : headroom.ToString(CultureInfo.InvariantCulture))}";

        return ValueTask.FromResult(new RoutingScoreResult(score, reason));
    }
}

/// <summary>
/// Weighted routing policy entry.
/// </summary>
public sealed record RoutingPolicyEntry(
    IModelRoutingPolicy Policy,
    double Weight = 1.0)
{
    public IModelRoutingPolicy Policy { get; init; } = Policy ?? throw new ArgumentNullException(nameof(Policy));

    public double Weight { get; init; } = Weight;
}

/// <summary>
/// Aggregates multiple routing policies into a weighted score.
/// </summary>
public sealed class CompositeModelRoutingPolicy : IModelRoutingPolicy
{
    private readonly RoutingPolicyEntry[] _policies;

    public CompositeModelRoutingPolicy(params IModelRoutingPolicy[] policies)
        : this(policies.Select(static policy => new RoutingPolicyEntry(policy)).ToArray())
    {
    }

    public CompositeModelRoutingPolicy(IEnumerable<RoutingPolicyEntry> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        _policies = policies.ToArray();
        if (_policies.Length == 0)
            throw new ArgumentException("At least one routing policy must be provided.", nameof(policies));

        if (_policies.Any(static entry => entry.Weight < 0))
            throw new ArgumentOutOfRangeException(nameof(policies), "Policy weights must be non-negative.");
    }

    public async ValueTask<RoutingScoreResult> ScoreAsync(
        RoutingCandidate candidate,
        RoutingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(context);

        var score = 0.0;
        var reasons = new List<string>(_policies.Length);
        foreach (var entry in _policies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await entry.Policy.ScoreAsync(candidate, context, cancellationToken).ConfigureAwait(false);
            if (double.IsNegativeInfinity(result.Score))
                return result;

            score += result.Score * entry.Weight;
            if (!string.IsNullOrWhiteSpace(result.Reason))
                reasons.Add(result.Reason);
        }

        return new RoutingScoreResult(score, reasons.Count == 0 ? null : string.Join("; ", reasons));
    }
}

/// <summary>
/// Explicit helpers for workflow-time route selection.
/// </summary>
public static class RoutingSteps
{
    public static async ValueTask<RoutingDecision?> SelectAsync(
        RoutingContext context,
        IModelRoutingPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var effectivePolicy = policy ?? new DefaultModelRoutingPolicy();
        var scored = new List<(RoutingCandidate Candidate, RoutingScoreResult Result)>(context.Candidates.Count);

        foreach (var candidate in context.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await effectivePolicy.ScoreAsync(candidate, context, cancellationToken).ConfigureAwait(false);
            if (double.IsNegativeInfinity(result.Score))
                continue;

            scored.Add((candidate, result));
        }

        var selected = scored
            .OrderByDescending(static item => item.Result.Score)
            .ThenBy(static item => item.Candidate.CandidateId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return selected.Candidate is null
            ? null
            : new RoutingDecision(selected.Candidate, selected.Result.Score, selected.Result.Reason);
    }
}
