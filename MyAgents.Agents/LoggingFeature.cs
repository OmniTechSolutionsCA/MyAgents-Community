using Microsoft.Extensions.Logging;
using MyAgents.Core;

namespace MyAgents.Agents;

/// <summary>
/// Feature that adds logging middleware to the agent pipeline.
/// </summary>
public sealed class LoggingFeature : IAgentFeature
{
    public void Contribute(AgentAssembly assembly)
    {
        assembly.Pipeline.Use(new LoggingMiddleware());
    }
}

/// <summary>
/// Middleware that logs agent turn execution.
/// </summary>
public sealed class LoggingMiddleware : IAgentMiddleware
{
    public async ValueTask<AgentTurnResult> InvokeAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>> next,
        CancellationToken cancellationToken = default)
    {
        var logger = context.Services.GetService<ILogger<LoggingMiddleware>>();
        logger?.LogInformation("Agent turn started for session {SessionId}", context.SessionId);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await next(context, cancellationToken);
            stopwatch.Stop();

            logger?.LogInformation(
                "Agent turn completed for session {SessionId} in {DurationMs}ms. Tool calls: {ToolCalls}",
                context.SessionId,
                stopwatch.ElapsedMilliseconds,
                result.Telemetry.ToolCalls);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger?.LogError(ex,
                "Agent turn failed for session {SessionId} after {DurationMs}ms",
                context.SessionId,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
