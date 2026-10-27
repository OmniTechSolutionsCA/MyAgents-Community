using System.Text.Json;
using MyAgents.Core;
using MyAgents.Core.Tools;

namespace MyAgents.Agents;

/// <summary>
/// Tool that enables an agent to synchronously delegate a specialized task to a subagent
/// and receive its distilled execution result.
/// </summary>
public sealed class DelegateToSubagentTool : ITool
{
    private readonly ISubagentDispatcher _dispatcher;

    public DelegateToSubagentTool(ISubagentDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public string Name => "delegate_to_subagent";

    public string? Description => "Delegates a specialized task to an isolated subagent and waits for its distilled output. Use when a specialized capability (e.g. coder, researcher, reviewer) is needed.";

    public string? ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "target_agent": {
              "type": "string",
              "description": "The registered name of the target subagent."
            },
            "instructions": {
              "type": "string",
              "description": "Detailed instructions and context for the subagent to execute."
            }
          },
          "required": ["target_agent", "instructions"]
        }
        """;

    public async ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonArguments);
            var targetAgent = doc.RootElement.GetProperty("target_agent").GetString();
            var instructions = doc.RootElement.GetProperty("instructions").GetString();

            if (string.IsNullOrWhiteSpace(targetAgent) || string.IsNullOrWhiteSpace(instructions))
            {
                return JsonSerializer.Serialize(new
                {
                    status = "error",
                    error = "target_agent and instructions must not be empty."
                });
            }

            var task = new SubagentTask(
                TaskId: Guid.NewGuid().ToString("N"),
                TargetAgentName: targetAgent,
                Instructions: instructions);

            var result = await _dispatcher.DelegateAsync(task, cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Serialize(new
            {
                task_id = result.TaskId,
                target_agent = result.TargetAgentName,
                status = result.Status.ToString().ToLowerInvariant(),
                output = result.FinalOutput,
                duration_ms = (long)result.Duration.TotalMilliseconds,
                total_tokens = result.TotalTokens,
                error = result.ErrorMessage
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                status = "error",
                error = ex.Message
            });
        }
    }
}

/// <summary>
/// Tool that enables an agent to fork a subagent task asynchronously in the background.
/// </summary>
public sealed class ForkSubagentTool : ITool
{
    private readonly ISubagentDispatcher _dispatcher;

    public ForkSubagentTool(ISubagentDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public string Name => "fork_subagent";

    public string? Description => "Starts a subagent task asynchronously in the background and returns a task_id immediately.";

    public string? ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "target_agent": {
              "type": "string",
              "description": "The registered name of the target subagent."
            },
            "instructions": {
              "type": "string",
              "description": "Detailed instructions and context for the subagent to execute."
            }
          },
          "required": ["target_agent", "instructions"]
        }
        """;

    public async ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonArguments);
            var targetAgent = doc.RootElement.GetProperty("target_agent").GetString();
            var instructions = doc.RootElement.GetProperty("instructions").GetString();

            if (string.IsNullOrWhiteSpace(targetAgent) || string.IsNullOrWhiteSpace(instructions))
            {
                return JsonSerializer.Serialize(new
                {
                    status = "error",
                    error = "target_agent and instructions must not be empty."
                });
            }

            var task = new SubagentTask(
                TaskId: Guid.NewGuid().ToString("N"),
                TargetAgentName: targetAgent,
                Instructions: instructions);

            var handle = await _dispatcher.ForkAsync(task, cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Serialize(new
            {
                task_id = handle.TaskId,
                target_agent = handle.TargetAgentName,
                status = handle.Status.ToString().ToLowerInvariant(),
                created_at = handle.CreatedAt
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                status = "error",
                error = ex.Message
            });
        }
    }
}

/// <summary>
/// Tool that allows an agent to check the status or retrieve the result of an asynchronously forked subagent task.
/// </summary>
public sealed class GetSubagentTaskStatusTool : ITool
{
    private readonly ISubagentDispatcher _dispatcher;

    public GetSubagentTaskStatusTool(ISubagentDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public string Name => "get_subagent_task_status";

    public string? Description => "Retrieves the current execution status or completed result of a background subagent task.";

    public string? ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "task_id": {
              "type": "string",
              "description": "The task ID returned when forking the subagent."
            }
          },
          "required": ["task_id"]
        }
        """;

    public async ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonArguments);
            var taskId = doc.RootElement.GetProperty("task_id").GetString();

            if (string.IsNullOrWhiteSpace(taskId))
            {
                return JsonSerializer.Serialize(new
                {
                    status = "error",
                    error = "task_id must not be empty."
                });
            }

            var result = await _dispatcher.GetTaskResultAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                return JsonSerializer.Serialize(new
                {
                    status = "not_found",
                    task_id = taskId
                });
            }

            return JsonSerializer.Serialize(new
            {
                task_id = result.TaskId,
                target_agent = result.TargetAgentName,
                status = result.Status.ToString().ToLowerInvariant(),
                output = result.FinalOutput,
                duration_ms = (long)result.Duration.TotalMilliseconds,
                total_tokens = result.TotalTokens,
                error = result.ErrorMessage
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                status = "error",
                error = ex.Message
            });
        }
    }
}

/// <summary>
/// Feature that equips an agent with subagent delegation tools (<c>delegate_to_subagent</c>, <c>fork_subagent</c>, <c>get_subagent_task_status</c>).
/// </summary>
public sealed class SubagentDelegationFeature : IAgentFeature
{
    private readonly ISubagentDispatcher _dispatcher;

    public SubagentDelegationFeature(ISubagentDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void Contribute(AgentAssembly assembly)
    {
        assembly.Tools.Add(new DelegateToSubagentTool(_dispatcher));
        assembly.Tools.Add(new ForkSubagentTool(_dispatcher));
        assembly.Tools.Add(new GetSubagentTaskStatusTool(_dispatcher));
        assembly.Capabilities.Add(AgentCapabilities.Tools);
    }
}