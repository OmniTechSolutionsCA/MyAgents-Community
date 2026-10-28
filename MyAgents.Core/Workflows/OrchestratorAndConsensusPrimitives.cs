using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MyAgents.Core.Workflows;

/// <summary>
/// Configuration for orchestrated subagent execution.
/// </summary>
public sealed record OrchestratorOptions(
    string PlannerAgentName,
    IReadOnlyList<string> WorkerAgentNames,
    string? SynthesizerAgentName = null,
    int MaxSteps = 8,
    int MaxRepairAttempts = 1,
    bool AllowParallelExecution = false,
    string? AdditionalInstructions = null)
{
    public string EffectiveSynthesizerAgentName
        => string.IsNullOrWhiteSpace(SynthesizerAgentName) ? PlannerAgentName : SynthesizerAgentName;
}

/// <summary>
/// Structured planner output for a single orchestration step.
/// </summary>
public sealed record OrchestratorStepModel(
    string Name,
    string Instruction,
    string WorkerAgentName,
    bool CanRunInParallel = false);

/// <summary>
/// Planned set of orchestration steps.
/// </summary>
public sealed record OrchestratorPlan(
    IReadOnlyList<OrchestratorStepModel> Steps)
{
    public IReadOnlyList<OrchestratorStepModel> Steps { get; init; } = Steps ?? throw new ArgumentNullException(nameof(Steps));
}

/// <summary>
/// Result for an executed orchestration step.
/// </summary>
public sealed record OrchestratorStepResult(
    OrchestratorStepModel Step,
    string WorkerAgentName,
    AgentTurnResult Result,
    int BatchNumber)
{
    public OrchestratorStepModel Step { get; init; } = Step ?? throw new ArgumentNullException(nameof(Step));

    public string WorkerAgentName { get; init; } = WorkerAgentName ?? throw new ArgumentNullException(nameof(WorkerAgentName));

    public AgentTurnResult Result { get; init; } = Result ?? throw new ArgumentNullException(nameof(Result));
}

/// <summary>
/// Result of a full orchestrated run.
/// </summary>
public sealed record OrchestratorResult(
    OrchestratorPlan Plan,
    IReadOnlyList<OrchestratorStepResult> StepResults,
    AgentTurnResult FinalResult)
{
    public OrchestratorPlan Plan { get; init; } = Plan ?? throw new ArgumentNullException(nameof(Plan));

    public IReadOnlyList<OrchestratorStepResult> StepResults { get; init; } = StepResults ?? throw new ArgumentNullException(nameof(StepResults));

    public AgentTurnResult FinalResult { get; init; } = FinalResult ?? throw new ArgumentNullException(nameof(FinalResult));
}

/// <summary>
/// Candidate produced during deterministic consensus.
/// </summary>
public sealed record ConsensusCandidate(
    string SourceAgentName,
    AgentTurnResult Result,
    string Choice)
{
    public string SourceAgentName { get; init; } = SourceAgentName ?? throw new ArgumentNullException(nameof(SourceAgentName));

    public AgentTurnResult Result { get; init; } = Result ?? throw new ArgumentNullException(nameof(Result));

    public string Choice { get; init; } = Choice ?? throw new ArgumentNullException(nameof(Choice));
}

/// <summary>
/// Result of a deterministic majority vote.
/// </summary>
public sealed record ConsensusResult(
    IReadOnlyList<ConsensusCandidate> Candidates,
    string WinningChoice,
    IReadOnlyList<ConsensusCandidate> WinningCandidates,
    bool TieBroken)
{
    public IReadOnlyList<ConsensusCandidate> Candidates { get; init; } = Candidates ?? throw new ArgumentNullException(nameof(Candidates));

    public string WinningChoice { get; init; } = WinningChoice ?? throw new ArgumentNullException(nameof(WinningChoice));

    public IReadOnlyList<ConsensusCandidate> WinningCandidates { get; init; } = WinningCandidates ?? throw new ArgumentNullException(nameof(WinningCandidates));
}

/// <summary>
/// Explicit orchestration helpers for planner-led subagent execution.
/// </summary>
public static class OrchestratorSteps
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async ValueTask<OrchestratorResult> RunAsync<TState>(
        IWorkflowSession<TState> session,
        OrchestratorOptions options,
        IReadOnlyList<AgentMessage> initialMessages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(initialMessages);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PlannerAgentName);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxSteps, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxRepairAttempts, 0);
        if (options.WorkerAgentNames is null || options.WorkerAgentNames.Count == 0)
            throw new ArgumentException("At least one worker agent name is required.", nameof(options));

        var workerNames = options.WorkerAgentNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (workerNames.Length == 0)
            throw new ArgumentException("At least one worker agent name is required.", nameof(options));

        var plan = new OrchestratorPlan(await RequestPlanAsync(
            session,
            options with { WorkerAgentNames = workerNames },
            initialMessages,
            cancellationToken).ConfigureAwait(false));

        var results = new List<OrchestratorStepResult>(plan.Steps.Count);
        var batchNumber = 0;
        var index = 0;

        while (index < plan.Steps.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = plan.Steps[index];

            if (options.AllowParallelExecution && step.CanRunInParallel)
            {
                var batch = new List<OrchestratorStepModel>();
                while (index < plan.Steps.Count && plan.Steps[index].CanRunInParallel)
                {
                    batch.Add(plan.Steps[index]);
                    index++;
                }

                batchNumber++;
                var batchResults = await ExecuteBatchAsync(
                    session,
                    batch,
                    batchNumber,
                    initialMessages,
                    results,
                    cancellationToken).ConfigureAwait(false);
                results.AddRange(batchResults);
                continue;
            }

            batchNumber++;
            var single = await ExecuteStepAsync(
                session,
                step,
                batchNumber,
                initialMessages,
                results,
                cancellationToken).ConfigureAwait(false);
            results.Add(single);
            index++;
        }

        var finalResult = await session.CallAgentAsync(
            options.EffectiveSynthesizerAgentName,
            BuildSynthesisMessages(initialMessages, plan, results),
            cancellationToken).ConfigureAwait(false);

        return new OrchestratorResult(plan, results.AsReadOnly(), finalResult);
    }

    private static async ValueTask<OrchestratorStepModel[]> RequestPlanAsync<TState>(
        IWorkflowSession<TState> session,
        OrchestratorOptions options,
        IReadOnlyList<AgentMessage> initialMessages,
        CancellationToken cancellationToken)
    {
        var plannerMessages = BuildPlannerMessages(initialMessages, options);
        for (var attempt = 0; attempt <= options.MaxRepairAttempts; attempt++)
        {
            var plannerResult = await session.CallAgentAsync(
                options.PlannerAgentName,
                plannerMessages,
                cancellationToken).ConfigureAwait(false);

            var payload = string.Join(Environment.NewLine, plannerResult.Output.Select(static message => message.Content));
            if (TryParsePlan(payload, out var steps, out var error))
                return ValidatePlan(steps, options);

            if (attempt == options.MaxRepairAttempts)
                throw new InvalidOperationException($"Planner output could not be parsed into an orchestration plan. {error}".Trim());

            plannerMessages =
            [
                .. initialMessages,
                new AgentMessage(
                    "user",
                    $$"""
                    Repair the previous response so it is valid JSON matching the requested orchestration schema.
                    Return only the repaired JSON array.

                    Validation error:
                    {{error}}

                    Previous response:
                    {{payload}}
                    """)
            ];
        }

        throw new InvalidOperationException("Planner output repair terminated unexpectedly.");
    }

    private static IReadOnlyList<AgentMessage> BuildPlannerMessages(
        IReadOnlyList<AgentMessage> initialMessages,
        OrchestratorOptions options)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Create an orchestration plan as a JSON array.");
        builder.AppendLine("Return ONLY JSON.");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Allowed worker agents: {string.Join(", ", options.WorkerAgentNames)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Limit the plan to at most {options.MaxSteps} steps.");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Parallel execution enabled: {options.AllowParallelExecution}");
        builder.AppendLine("""
            JSON schema:
            [
              {
                "name": "research",
                "instruction": "Research the topic and gather evidence.",
                "workerAgentName": "researcher",
                "canRunInParallel": false
              }
            ]
            """);

        if (!string.IsNullOrWhiteSpace(options.AdditionalInstructions))
        {
            builder.AppendLine("Additional instructions:");
            builder.AppendLine(options.AdditionalInstructions);
        }

        return
        [
            .. initialMessages,
            new AgentMessage("user", builder.ToString().TrimEnd())
        ];
    }

    private static bool TryParsePlan(
        string payload,
        out OrchestratorStepModel[] steps,
        out string error)
    {
        try
        {
            var normalized = ExtractJsonArray(payload);
            steps = JsonSerializer.Deserialize<OrchestratorStepModel[]>(normalized, s_jsonOptions)
                ?? Array.Empty<OrchestratorStepModel>();
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            steps = Array.Empty<OrchestratorStepModel>();
            error = ex.Message;
            return false;
        }
    }

    private static OrchestratorStepModel[] ValidatePlan(
        OrchestratorStepModel[] steps,
        OrchestratorOptions options)
    {
        if (steps.Length == 0)
            throw new InvalidOperationException("Planner returned an empty orchestration plan.");

        if (steps.Length > options.MaxSteps)
            throw new InvalidOperationException($"Planner returned {steps.Length} steps, which exceeds the configured limit of {options.MaxSteps}.");

        var duplicateName = steps
            .GroupBy(static step => step.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateName is not null)
            throw new InvalidOperationException($"Planner returned duplicate step name '{duplicateName.Key}'.");

        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.Name))
                throw new InvalidOperationException("Planner returned a step with no name.");

            if (string.IsNullOrWhiteSpace(step.Instruction))
                throw new InvalidOperationException($"Planner returned step '{step.Name}' with no instruction.");

            if (string.IsNullOrWhiteSpace(step.WorkerAgentName))
                throw new InvalidOperationException($"Planner returned step '{step.Name}' with no worker agent.");

            if (!options.WorkerAgentNames.Contains(step.WorkerAgentName, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Planner returned unknown worker agent '{step.WorkerAgentName}' for step '{step.Name}'.");
            }
        }

        return steps;
    }

    private static async ValueTask<IReadOnlyList<OrchestratorStepResult>> ExecuteBatchAsync<TState>(
        IWorkflowSession<TState> session,
        IReadOnlyList<OrchestratorStepModel> batch,
        int batchNumber,
        IReadOnlyList<AgentMessage> initialMessages,
        IReadOnlyList<OrchestratorStepResult> priorResults,
        CancellationToken cancellationToken)
    {
        var tasks = batch
            .Select(step => ExecuteStepAsync(session, step, batchNumber, initialMessages, priorResults, cancellationToken).AsTask())
            .ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    private static async ValueTask<OrchestratorStepResult> ExecuteStepAsync<TState>(
        IWorkflowSession<TState> session,
        OrchestratorStepModel step,
        int batchNumber,
        IReadOnlyList<AgentMessage> initialMessages,
        IReadOnlyList<OrchestratorStepResult> priorResults,
        CancellationToken cancellationToken)
    {
        var result = await session.CallAgentAsync(
            step.WorkerAgentName,
            BuildWorkerMessages(initialMessages, step, priorResults),
            cancellationToken).ConfigureAwait(false);

        return new OrchestratorStepResult(step, step.WorkerAgentName, result, batchNumber);
    }

    private static IReadOnlyList<AgentMessage> BuildWorkerMessages(
        IReadOnlyList<AgentMessage> initialMessages,
        OrchestratorStepModel step,
        IReadOnlyList<OrchestratorStepResult> priorResults)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Current orchestration step: {step.Name}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Worker agent: {step.WorkerAgentName}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Instruction: {step.Instruction}");
        builder.AppendLine();
        builder.AppendLine("Completed step summaries:");

        if (priorResults.Count == 0)
        {
            builder.AppendLine("- None");
        }
        else
        {
            foreach (var prior in priorResults)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- {prior.Step.Name} [{prior.WorkerAgentName}]: {FlattenMessages(prior.Result.Output)}");
            }
        }

        return
        [
            .. initialMessages,
            new AgentMessage("user", builder.ToString().TrimEnd())
        ];
    }

    private static IReadOnlyList<AgentMessage> BuildSynthesisMessages(
        IReadOnlyList<AgentMessage> initialMessages,
        OrchestratorPlan plan,
        IReadOnlyList<OrchestratorStepResult> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Synthesize the final answer using the completed orchestration plan and outputs.");
        builder.AppendLine("Plan:");
        foreach (var step in plan.Steps)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {step.Name} [{step.WorkerAgentName}] parallel={step.CanRunInParallel}: {step.Instruction}");
        }

        builder.AppendLine();
        builder.AppendLine("Step outputs:");
        foreach (var result in results)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Batch {result.BatchNumber} :: {result.Step.Name} [{result.WorkerAgentName}]");
            builder.AppendLine(CultureInfo.InvariantCulture, $"  {FlattenMessages(result.Result.Output)}");
        }

        return
        [
            .. initialMessages,
            new AgentMessage("user", builder.ToString().TrimEnd())
        ];
    }

    private static string ExtractJsonArray(string payload)
    {
        var trimmed = payload.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineEnd = trimmed.IndexOf('\n');
            if (firstLineEnd >= 0)
                trimmed = trimmed[(firstLineEnd + 1)..];

            var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFence >= 0)
                trimmed = trimmed[..closingFence];
        }

        var start = trimmed.IndexOf('[');
        var end = trimmed.LastIndexOf(']');
        if (start < 0 || end < start)
            throw new InvalidOperationException("Planner response did not contain a JSON array.");

        return trimmed[start..(end + 1)];
    }

    private static string FlattenMessages(IReadOnlyList<AgentMessage> messages)
        => string.Join(
            Environment.NewLine,
            messages
                .Where(static message => !string.IsNullOrWhiteSpace(message.Content))
                .Select(static message => $"[{message.Role}] {message.Content}"));
}

/// <summary>
/// Explicit helpers for deterministic consensus and majority voting.
/// </summary>
public static class ConsensusSteps
{
    public static async ValueTask<ConsensusResult> RunMajorityVoteAsync<TState>(
        IWorkflowSession<TState> session,
        IReadOnlyList<string> agentNames,
        IReadOnlyList<AgentMessage> messages,
        Func<AgentTurnResult, string> choiceSelector,
        Func<IReadOnlyList<ConsensusCandidate>, ConsensusCandidate>? tieBreaker = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(agentNames);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(choiceSelector);
        if (agentNames.Count == 0)
            throw new ArgumentException("At least one agent name is required.", nameof(agentNames));

        var tasks = agentNames
            .Select((agentName, index) => InvokeCandidateAsync(session, agentName, index, messages, choiceSelector, cancellationToken).AsTask())
            .ToArray();

        var ordered = (await Task.WhenAll(tasks).ConfigureAwait(false))
            .OrderBy(static item => item.Index)
            .Select(static item => item.Candidate)
            .ToArray();

        return ReduceMajority(ordered, tieBreaker);
    }

    public static ConsensusResult ReduceMajority(
        IReadOnlyList<ConsensusCandidate> candidates,
        Func<IReadOnlyList<ConsensusCandidate>, ConsensusCandidate>? tieBreaker = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
            throw new ArgumentException("At least one consensus candidate is required.", nameof(candidates));

        var ranked = candidates
            .Select((candidate, index) => new { Candidate = candidate, Index = index })
            .GroupBy(static item => item.Candidate.Choice, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Choice = group.Key,
                Count = group.Count(),
                FirstIndex = group.Min(static item => item.Index),
                Candidates = group
                    .OrderBy(static item => item.Index)
                    .Select(static item => item.Candidate)
                    .ToArray()
            })
            .OrderByDescending(static group => group.Count)
            .ThenBy(static group => group.FirstIndex)
            .ToArray();

        var top = ranked[0];
        var tied = ranked
            .Where(group => group.Count == top.Count)
            .OrderBy(static group => group.FirstIndex)
            .ToArray();

        if (tied.Length == 1)
            return new ConsensusResult(candidates, top.Choice, top.Candidates, false);

        var tieCandidates = tied.Select(group => group.Candidates[0]).ToArray();
        var winner = tieBreaker is null
            ? tieCandidates[0]
            : tieBreaker(tieCandidates);

        var winningGroup = ranked.First(group =>
            string.Equals(group.Choice, winner.Choice, StringComparison.OrdinalIgnoreCase));

        return new ConsensusResult(candidates, winningGroup.Choice, winningGroup.Candidates, true);
    }

    private static async ValueTask<(int Index, ConsensusCandidate Candidate)> InvokeCandidateAsync<TState>(
        IWorkflowSession<TState> session,
        string agentName,
        int index,
        IReadOnlyList<AgentMessage> messages,
        Func<AgentTurnResult, string> choiceSelector,
        CancellationToken cancellationToken)
    {
        var result = await session.CallAgentAsync(agentName, messages, cancellationToken).ConfigureAwait(false);
        var choice = choiceSelector(result);
        if (string.IsNullOrWhiteSpace(choice))
            throw new InvalidOperationException($"Consensus choice for agent '{agentName}' was empty.");

        return (index, new ConsensusCandidate(agentName, result, choice.Trim()));
    }
}
