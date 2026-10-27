using System.Text.Json;

namespace MyAgents.Core.Tools;

/// <summary>
/// A tool that allows an agent to explicitly hand off the conversation to another agent.
/// The orchestrator (e.g., <see cref="Workflows.WorkflowSteps.RunHandoffAsync"/>)
/// can detect this tool call and perform the switch.
/// </summary>
public sealed class HandoffTool : ITool
{
    public string Name => "handoff_to";

    public string? Description => "Hands off the conversation to another specialized agent. Use this when the task requires a different set of capabilities.";

    public string? ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "agent_name": {
              "type": "string",
              "description": "The name of the agent to hand off to."
            },
            "reason": {
              "type": "string",
              "description": "Optional reason for the handoff."
            }
          },
          "required": ["agent_name"]
        }
        """;

    public ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        // The tool itself doesn't do the handoff; it just records the intent in the transcript.
        // The orchestrator is responsible for seeing this and switching.
        try
        {
            using var doc = JsonDocument.Parse(jsonArguments);
            var agentName = doc.RootElement.GetProperty("agent_name").GetString();
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;

            return ValueTask.FromResult(JsonSerializer.Serialize(new
            {
                status = "handoff_initiated",
                target_agent = agentName,
                reason = reason
            }));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(JsonSerializer.Serialize(new
            {
                status = "error",
                message = ex.Message
            }));
        }
    }
}
