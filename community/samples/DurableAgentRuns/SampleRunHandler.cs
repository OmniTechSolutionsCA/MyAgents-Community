using MyAgents.Core.Execution;
using MyAgents.Runtime.Workers;

internal sealed class SampleRunHandler : IAgentRunHandler
{
    public const string StableHandlerId = "sample.long-running.v1";

    public string HandlerId => StableHandlerId;

    public async ValueTask<AgentRunHandlerResult> ExecuteAsync(
        AgentRunContext context,
        CancellationToken cancellationToken = default)
    {
        var input = System.Text.Json.JsonSerializer.Deserialize<SampleInput>(context.InputJson)
            ?? throw new InvalidOperationException("The sample input was invalid.");
        await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        return AgentRunHandlerResult.Completed(System.Text.Json.JsonSerializer.Serialize(new { input.Message, context.RunId, context.Attempt }));
    }
}
