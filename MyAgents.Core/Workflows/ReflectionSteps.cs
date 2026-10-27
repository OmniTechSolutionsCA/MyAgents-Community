namespace MyAgents.Core.Workflows;

/// <summary>
/// Options for running a reflection loop between a generator agent and a critic agent.
/// </summary>
public sealed record ReflectionOptions(
    string GeneratorAgentName,
    string CriticAgentName,
    int MaxIterations = 3,
    Func<AgentTurnResult, AgentTurnResult, int, bool>? ApprovalPredicate = null,
    Func<IReadOnlyList<AgentMessage>, AgentTurnResult, AgentTurnResult, int, IReadOnlyList<AgentMessage>>? BuildRefinementMessages = null)
{
    public bool IsApproved(AgentTurnResult candidate, AgentTurnResult critique, int iteration)
        => (ApprovalPredicate ?? ReflectionSteps.ContainsApprovedCritique)(candidate, critique, iteration);

    public IReadOnlyList<AgentMessage> BuildMessages(
        IReadOnlyList<AgentMessage> initialMessages,
        AgentTurnResult candidate,
        AgentTurnResult critique,
        int iteration)
        => (BuildRefinementMessages ?? ReflectionSteps.BuildDefaultRefinementMessages)(
            initialMessages,
            candidate,
            critique,
            iteration);
}

/// <summary>
/// Captures a single reflection iteration.
/// </summary>
public sealed record ReflectionIteration(
    int IterationNumber,
    AgentTurnResult CandidateResult,
    AgentTurnResult CritiqueResult,
    bool Approved);

/// <summary>
/// Result of a completed reflection loop.
/// </summary>
public sealed record ReflectionResult(
    IReadOnlyList<ReflectionIteration> Iterations,
    AgentTurnResult FinalCandidate,
    AgentTurnResult? FinalCritique,
    bool Approved,
    int IterationsCompleted);

/// <summary>
/// Workflow helpers for running an explicit generate/critique/refine loop.
/// </summary>
public static class ReflectionSteps
{
    public static async ValueTask<ReflectionResult> RunAsync<TState>(
        IWorkflowSession<TState> session,
        ReflectionOptions options,
        IReadOnlyList<AgentMessage> initialMessages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(initialMessages);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.GeneratorAgentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CriticAgentName);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxIterations, 1);

        var iterations = new List<ReflectionIteration>(options.MaxIterations);
        var candidateMessages = initialMessages;
        AgentTurnResult? finalCandidate = null;
        AgentTurnResult? finalCritique = null;

        for (var iteration = 1; iteration <= options.MaxIterations; iteration++)
        {
            finalCandidate = await session.CallAgentAsync(
                options.GeneratorAgentName,
                candidateMessages,
                cancellationToken).ConfigureAwait(false);

            var critiqueMessages = BuildCritiqueMessages(initialMessages, finalCandidate, iteration);
            finalCritique = await session.CallAgentAsync(
                options.CriticAgentName,
                critiqueMessages,
                cancellationToken).ConfigureAwait(false);

            var approved = options.IsApproved(finalCandidate, finalCritique, iteration);
            iterations.Add(new ReflectionIteration(iteration, finalCandidate, finalCritique, approved));

            if (approved)
            {
                return new ReflectionResult(
                    Iterations: iterations.AsReadOnly(),
                    FinalCandidate: finalCandidate,
                    FinalCritique: finalCritique,
                    Approved: true,
                    IterationsCompleted: iteration);
            }

            candidateMessages = options.BuildMessages(initialMessages, finalCandidate, finalCritique, iteration);
        }

        return new ReflectionResult(
            Iterations: iterations.AsReadOnly(),
            FinalCandidate: finalCandidate!,
            FinalCritique: finalCritique,
            Approved: false,
            IterationsCompleted: iterations.Count);
    }

    /// <summary>
    /// Built-in approval predicate that treats any critic output containing "APPROVED"
    /// as a successful critique.
    /// </summary>
    public static bool ContainsApprovedCritique(
        AgentTurnResult candidate,
        AgentTurnResult critique,
        int iteration)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(critique);

        return critique.Output.Any(static message =>
            message.Content.Contains("APPROVED", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Default refinement-message builder that appends the candidate output and the critique
    /// as user-visible context for the next generator turn.
    /// </summary>
    public static IReadOnlyList<AgentMessage> BuildDefaultRefinementMessages(
        IReadOnlyList<AgentMessage> initialMessages,
        AgentTurnResult candidate,
        AgentTurnResult critique,
        int iteration)
    {
        ArgumentNullException.ThrowIfNull(initialMessages);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(critique);

        var critiqueText = FlattenMessages(critique.Output);
        return initialMessages
            .Concat(candidate.Output)
            .Append(new AgentMessage(
                "user",
                $"Revise your previous answer using this critique from iteration {iteration}:{Environment.NewLine}{critiqueText}"))
            .ToArray();
    }

    private static IReadOnlyList<AgentMessage> BuildCritiqueMessages(
        IReadOnlyList<AgentMessage> initialMessages,
        AgentTurnResult candidate,
        int iteration)
    {
        var candidateText = FlattenMessages(candidate.Output);
        return initialMessages
            .Append(new AgentMessage(
                "user",
                $"Critique the following candidate from iteration {iteration}. Respond with APPROVED if it is acceptable, otherwise explain the defects clearly.{Environment.NewLine}{Environment.NewLine}{candidateText}"))
            .ToArray();
    }

    private static string FlattenMessages(IReadOnlyList<AgentMessage> messages)
        => string.Join(
            Environment.NewLine,
            messages
                .Where(static message => !string.IsNullOrWhiteSpace(message.Content))
                .Select(static message => $"[{message.Role}] {message.Content}"));
}
