using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using MyAgents.Core;

namespace MyAgents.Agents;

/// <summary>
/// Hierarchical subagent dispatcher that manages child agent sessions,
/// isolated context execution, delegation depth recursion guards, and background task forking.
/// </summary>
public sealed class HierarchicalSubagentDispatcher : ISubagentDispatcher, IDisposable
{
    private readonly IAgentSessionFactory _sessionFactory;
    private readonly Dictionary<string, AgentDefinition> _subagents;
    private readonly SubagentOrchestratorOptions _options;
    private readonly ConcurrentDictionary<string, BackgroundTaskEntry> _backgroundTasks = new(StringComparer.OrdinalIgnoreCase);

    public HierarchicalSubagentDispatcher(
        IAgentSessionFactory sessionFactory,
        IEnumerable<AgentDefinition> subagents,
        SubagentOrchestratorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(subagents);

        _sessionFactory = sessionFactory;
        _subagents = subagents.ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase);
        _options = options ?? new SubagentOrchestratorOptions();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetAvailableSubagentNames()
    {
        return _subagents.Keys.ToArray();
    }

    /// <inheritdoc />
    public AgentDefinition? GetSubagentDefinition(string agentName)
    {
        ArgumentNullException.ThrowIfNull(agentName);
        _subagents.TryGetValue(agentName, out var def);
        return def;
    }

    /// <inheritdoc />
    public async ValueTask<SubagentResult> DelegateAsync(SubagentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.CurrentDepth > _options.MaxDelegationDepth)
        {
            throw new MaxDelegationDepthExceededException(task.CurrentDepth, _options.MaxDelegationDepth);
        }

        if (!_subagents.TryGetValue(task.TargetAgentName, out var definition))
        {
            throw new KeyNotFoundException($"Subagent '{task.TargetAgentName}' is not registered with the dispatcher.");
        }

        var timeout = task.Timeout ?? _options.DefaultTimeout;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout);

        var childSessionId = FormatChildSessionId(task);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await using var session = _sessionFactory.CreateSession(childSessionId, definition);

            var messages = PrepareInputMessages(task);
            var turnResult = await session.InvokeAsync(messages, linkedCts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            var assistantMsg = turnResult.Output.LastOrDefault(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                               ?? (turnResult.Output.Count > 0 ? turnResult.Output[^1] : null);
            var finalOutput = assistantMsg?.Content;

            return new SubagentResult(
                TaskId: task.TaskId,
                TargetAgentName: task.TargetAgentName,
                Status: SubagentExecutionState.Completed,
                FinalOutput: finalOutput,
                OutputMessages: turnResult.Output,
                Duration: stopwatch.Elapsed,
                PromptTokens: turnResult.Telemetry.PromptTokens,
                CompletionTokens: turnResult.Telemetry.CompletionTokens,
                TotalTokens: turnResult.Telemetry.TotalTokens);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new SubagentResult(
                TaskId: task.TaskId,
                TargetAgentName: task.TargetAgentName,
                Status: SubagentExecutionState.Canceled,
                FinalOutput: null,
                OutputMessages: [],
                Duration: stopwatch.Elapsed,
                ErrorMessage: "Subagent execution canceled by caller.");
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new SubagentResult(
                TaskId: task.TaskId,
                TargetAgentName: task.TargetAgentName,
                Status: SubagentExecutionState.Failed,
                FinalOutput: null,
                OutputMessages: [],
                Duration: stopwatch.Elapsed,
                ErrorMessage: $"Subagent execution timed out after {timeout.TotalSeconds:F1}s.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new SubagentResult(
                TaskId: task.TaskId,
                TargetAgentName: task.TargetAgentName,
                Status: SubagentExecutionState.Failed,
                FinalOutput: null,
                OutputMessages: [],
                Duration: stopwatch.Elapsed,
                ErrorMessage: ex.Message);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentTurnResult> DelegateStreamingAsync(
        SubagentTask task,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.CurrentDepth > _options.MaxDelegationDepth)
        {
            throw new MaxDelegationDepthExceededException(task.CurrentDepth, _options.MaxDelegationDepth);
        }

        if (!_subagents.TryGetValue(task.TargetAgentName, out var definition))
        {
            throw new KeyNotFoundException($"Subagent '{task.TargetAgentName}' is not registered with the dispatcher.");
        }

        var timeout = task.Timeout ?? _options.DefaultTimeout;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout);

        var childSessionId = FormatChildSessionId(task);
        await using var session = _sessionFactory.CreateSession(childSessionId, definition);
        var messages = PrepareInputMessages(task);

        await foreach (var chunk in session.InvokeStreamingAsync(messages, linkedCts.Token).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    /// <inheritdoc />
    public ValueTask<SubagentTaskHandle> ForkAsync(SubagentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var taskId = string.IsNullOrWhiteSpace(task.TaskId) ? Guid.NewGuid().ToString("N") : task.TaskId;
        var normalizedTask = task with { TaskId = taskId };

        var cts = new CancellationTokenSource();
        var entry = new BackgroundTaskEntry(normalizedTask, cts);
        _backgroundTasks[taskId] = entry;

        // Start background execution
        entry.ExecutionTask = Task.Run(async () =>
        {
            entry.State = SubagentExecutionState.Running;
            var result = await DelegateAsync(normalizedTask, cts.Token).ConfigureAwait(false);
            entry.Result = result;
            entry.State = result.Status;
            return result;
        });

        var handle = new SubagentTaskHandle(taskId, normalizedTask.TargetAgentName, SubagentExecutionState.Running, DateTimeOffset.UtcNow);
        return ValueTask.FromResult(handle);
    }

    /// <inheritdoc />
    public ValueTask<SubagentResult?> GetTaskResultAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskId);

        if (!_backgroundTasks.TryGetValue(taskId, out var entry))
        {
            return ValueTask.FromResult<SubagentResult?>(null);
        }

        if (entry.Result is not null)
        {
            return ValueTask.FromResult<SubagentResult?>(entry.Result);
        }

        // Return snapshot while in flight
        var pending = new SubagentResult(
            TaskId: taskId,
            TargetAgentName: entry.Task.TargetAgentName,
            Status: entry.State,
            FinalOutput: null,
            OutputMessages: [],
            Duration: TimeSpan.Zero);

        return ValueTask.FromResult<SubagentResult?>(pending);
    }

    /// <inheritdoc />
    public ValueTask<bool> CancelTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskId);

        if (_backgroundTasks.TryGetValue(taskId, out var entry))
        {
            entry.Cts.Cancel();
            entry.State = SubagentExecutionState.Canceled;
            return ValueTask.FromResult(true);
        }

        return ValueTask.FromResult(false);
    }

    private string FormatChildSessionId(SubagentTask task)
    {
        if (_options.PrefixSubagentSessionId && !string.IsNullOrWhiteSpace(task.ParentSessionId))
        {
            return $"{task.ParentSessionId}:sub:{task.TargetAgentName}:{task.TaskId}";
        }

        return $"sub:{task.TargetAgentName}:{task.TaskId}";
    }

    private static List<AgentMessage> PrepareInputMessages(SubagentTask task)
    {
        var list = new List<AgentMessage>();
        if (task.InitialContext is { Count: > 0 })
        {
            list.AddRange(task.InitialContext);
        }

        list.Add(AgentMessage.FromText("user", task.Instructions));
        return list;
    }

    public void Dispose()
    {
        foreach (var entry in _backgroundTasks.Values)
        {
            entry.Cts.Cancel();
            entry.Cts.Dispose();
        }
        _backgroundTasks.Clear();
    }

    private sealed class BackgroundTaskEntry
    {
        public SubagentTask Task { get; }
        public CancellationTokenSource Cts { get; }
        public SubagentExecutionState State { get; set; } = SubagentExecutionState.Queued;
        public Task<SubagentResult>? ExecutionTask { get; set; }
        public SubagentResult? Result { get; set; }
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

        public BackgroundTaskEntry(SubagentTask task, CancellationTokenSource cts)
        {
            Task = task;
            Cts = cts;
        }
    }
}
