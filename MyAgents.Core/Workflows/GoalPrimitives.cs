namespace MyAgents.Core.Workflows;

/// <summary>
/// Priority assigned to a goal definition.
/// </summary>
public enum GoalPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3,
}

/// <summary>
/// Runtime lifecycle status for a goal.
/// </summary>
public enum GoalStatus
{
    Proposed = 0,
    Ready = 1,
    Active = 2,
    Blocked = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6,
}

/// <summary>
/// Immutable goal specification.
/// </summary>
public sealed record GoalDefinition
{
    public GoalDefinition(
        string name,
        string objective,
        GoalPriority priority = GoalPriority.Normal,
        IReadOnlyList<string>? successCriteria = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(objective);

        Name = name;
        Objective = objective;
        Priority = priority;
        SuccessCriteria = successCriteria?.ToArray() ?? Array.Empty<string>();
        Metadata = metadata is null
            ? null
            : new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase);
    }

    public string Name { get; init; }

    public string Objective { get; init; }

    public GoalPriority Priority { get; init; }

    public IReadOnlyList<string> SuccessCriteria { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Runtime goal record with mutable lifecycle state.
/// </summary>
public sealed record GoalRecord
{
    public GoalRecord(
        string goalId,
        GoalDefinition definition,
        GoalStatus status = GoalStatus.Proposed,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null,
        string? parentGoalId = null,
        string? statusReason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);
        GoalId = goalId;
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Status = status;
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
        UpdatedAt = updatedAt ?? CreatedAt;
        ParentGoalId = parentGoalId;
        StatusReason = statusReason;
    }

    public string GoalId { get; init; }

    public GoalDefinition Definition { get; init; }

    public GoalStatus Status { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string? ParentGoalId { get; init; }

    public string? StatusReason { get; init; }
}

/// <summary>
/// Lightweight goal container intended to live inside caller-managed workflow or graph state.
/// </summary>
public sealed class GoalCollection
{
    public List<GoalRecord> Goals { get; } = [];

    public GoalRecord Register(GoalRecord goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        if (Goals.Any(existing => string.Equals(existing.GoalId, goal.GoalId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Goal '{goal.GoalId}' is already registered.");

        Goals.Add(goal);
        return goal;
    }

    public GoalRecord Add(
        GoalDefinition definition,
        string? goalId = null,
        GoalStatus status = GoalStatus.Proposed,
        string? parentGoalId = null,
        string? statusReason = null,
        DateTimeOffset? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var timestamp = createdAt ?? DateTimeOffset.UtcNow;
        var record = new GoalRecord(
            goalId: string.IsNullOrWhiteSpace(goalId) ? $"goal-{Guid.NewGuid():N}" : goalId,
            definition: definition,
            status: status,
            createdAt: timestamp,
            updatedAt: timestamp,
            parentGoalId: parentGoalId,
            statusReason: statusReason);

        return Register(record);
    }

    public GoalRecord? Get(string goalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);
        return Goals.FirstOrDefault(goal => string.Equals(goal.GoalId, goalId, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<GoalRecord> ListByStatus(params GoalStatus[] statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        if (statuses.Length == 0)
            return Goals.AsReadOnly();

        var filter = statuses.ToHashSet();
        return Goals.Where(goal => filter.Contains(goal.Status)).ToArray();
    }

    public IReadOnlyList<GoalRecord> Ready()
        => ListByStatus(GoalStatus.Ready);

    public IReadOnlyList<GoalRecord> Active()
        => ListByStatus(GoalStatus.Active);

    public GoalRecord UpdateStatus(
        string goalId,
        GoalStatus status,
        string? reason = null,
        DateTimeOffset? updatedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);

        for (var index = 0; index < Goals.Count; index++)
        {
            if (!string.Equals(Goals[index].GoalId, goalId, StringComparison.OrdinalIgnoreCase))
                continue;

            var updated = Goals[index] with
            {
                Status = status,
                StatusReason = reason,
                UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow,
            };
            Goals[index] = updated;
            return updated;
        }

        throw new KeyNotFoundException($"Goal '{goalId}' is not registered.");
    }
}

/// <summary>
/// Context passed to goal prioritization policies.
/// </summary>
public sealed record GoalPrioritizationContext(
    GoalCollection Goals,
    IReadOnlyDictionary<string, object?>? Metadata = null)
{
    public GoalCollection Goals { get; init; } = Goals ?? throw new ArgumentNullException(nameof(Goals));

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; } = Metadata;
}

/// <summary>
/// Result returned from goal prioritization scoring.
/// </summary>
public sealed record GoalPrioritizationResult(
    double Score,
    string? Reason = null);

/// <summary>
/// Scores a goal against the current prioritization context.
/// </summary>
public interface IGoalPrioritizationPolicy
{
    ValueTask<GoalPrioritizationResult> ScoreAsync(
        GoalRecord goal,
        GoalPrioritizationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default deterministic prioritization policy.
/// Only ready goals are eligible; higher priority ranks first.
/// </summary>
public sealed class DefaultGoalPrioritizationPolicy : IGoalPrioritizationPolicy
{
    public ValueTask<GoalPrioritizationResult> ScoreAsync(
        GoalRecord goal,
        GoalPrioritizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (goal.Status != GoalStatus.Ready)
            return ValueTask.FromResult(new GoalPrioritizationResult(double.NegativeInfinity, $"Goal status '{goal.Status}' is not eligible."));

        return ValueTask.FromResult(new GoalPrioritizationResult(
            Score: goal.Definition.Priority switch
            {
                GoalPriority.Low => 1.0,
                GoalPriority.Normal => 2.0,
                GoalPriority.High => 3.0,
                GoalPriority.Critical => 4.0,
                _ => 0.0,
            },
            Reason: $"Priority '{goal.Definition.Priority}'"));
    }
}

/// <summary>
/// Weighted policy entry used by <see cref="CompositeGoalPrioritizationPolicy"/>.
/// </summary>
public sealed record GoalPrioritizationPolicyEntry(
    IGoalPrioritizationPolicy Policy,
    double Weight = 1.0)
{
    public IGoalPrioritizationPolicy Policy { get; init; } = Policy ?? throw new ArgumentNullException(nameof(Policy));

    public double Weight { get; init; } = Weight;
}

/// <summary>
/// Aggregates multiple prioritization policies into a single weighted score.
/// </summary>
public sealed class CompositeGoalPrioritizationPolicy : IGoalPrioritizationPolicy
{
    private readonly IReadOnlyList<GoalPrioritizationPolicyEntry> _policies;

    public CompositeGoalPrioritizationPolicy(params IGoalPrioritizationPolicy[] policies)
        : this(policies.Select(static policy => new GoalPrioritizationPolicyEntry(policy)).ToArray())
    {
    }

    public CompositeGoalPrioritizationPolicy(IEnumerable<GoalPrioritizationPolicyEntry> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        _policies = policies.ToArray();
        if (_policies.Count == 0)
            throw new ArgumentException("At least one prioritization policy must be provided.", nameof(policies));

        if (_policies.Any(static entry => entry.Weight < 0))
            throw new ArgumentOutOfRangeException(nameof(policies), "Policy weights must be non-negative.");
    }

    public async ValueTask<GoalPrioritizationResult> ScoreAsync(
        GoalRecord goal,
        GoalPrioritizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(context);

        var score = 0.0;
        var reasons = new List<string>(_policies.Count);
        foreach (var entry in _policies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await entry.Policy.ScoreAsync(goal, context, cancellationToken).ConfigureAwait(false);
            if (double.IsNegativeInfinity(result.Score))
                return result;

            score += result.Score * entry.Weight;
            if (!string.IsNullOrWhiteSpace(result.Reason))
                reasons.Add(result.Reason);
        }

        return new GoalPrioritizationResult(
            Score: score,
            Reason: reasons.Count == 0 ? null : string.Join("; ", reasons));
    }
}

/// <summary>
/// Workflow helpers for selecting and updating goals.
/// </summary>
public static class GoalSteps
{
    public static async ValueTask<GoalRecord?> SelectNextAsync(
        GoalCollection goals,
        IGoalPrioritizationPolicy? policy = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goals);

        var effectivePolicy = policy ?? new DefaultGoalPrioritizationPolicy();
        var context = new GoalPrioritizationContext(goals, metadata);
        var scored = new List<(GoalRecord Goal, GoalPrioritizationResult Result)>(goals.Goals.Count);

        foreach (var goal in goals.Goals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await effectivePolicy.ScoreAsync(goal, context, cancellationToken).ConfigureAwait(false);
            if (double.IsNegativeInfinity(result.Score))
                continue;

            scored.Add((goal, result));
        }

        return scored
            .OrderByDescending(static item => item.Result.Score)
            .ThenBy(static item => item.Goal.CreatedAt)
            .ThenBy(static item => item.Goal.GoalId, StringComparer.OrdinalIgnoreCase)
            .Select(static item => item.Goal)
            .FirstOrDefault();
    }

    public static GoalRecord MarkReady(GoalCollection goals, string goalId, string? reason = null)
        => goals.UpdateStatus(goalId, GoalStatus.Ready, reason);

    public static GoalRecord MarkBlocked(GoalCollection goals, string goalId, string? reason = null)
        => goals.UpdateStatus(goalId, GoalStatus.Blocked, reason);

    public static GoalRecord MarkCompleted(GoalCollection goals, string goalId, string? reason = null)
        => goals.UpdateStatus(goalId, GoalStatus.Completed, reason);

    public static GoalRecord MarkFailed(GoalCollection goals, string goalId, string? reason = null)
        => goals.UpdateStatus(goalId, GoalStatus.Failed, reason);

    public static IReadOnlyList<AgentMessage> BuildGoalMessages(
        GoalRecord goal,
        string? additionalInstruction = null)
    {
        ArgumentNullException.ThrowIfNull(goal);

        var lines = new List<string>
        {
            $"Goal: {goal.Definition.Name}",
            $"Objective: {goal.Definition.Objective}",
            $"Priority: {goal.Definition.Priority}",
        };

        if (goal.Definition.SuccessCriteria.Count > 0)
        {
            lines.Add("Success criteria:");
            lines.AddRange(goal.Definition.SuccessCriteria.Select(static criterion => $"- {criterion}"));
        }

        if (!string.IsNullOrWhiteSpace(additionalInstruction))
            lines.Add(additionalInstruction);

        return [new AgentMessage("user", string.Join(Environment.NewLine, lines))];
    }
}
