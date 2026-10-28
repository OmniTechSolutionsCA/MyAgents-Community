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
public sealed partial class LoggingMiddleware : IAgentMiddleware
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Agent turn started for session {SessionId}")]
    private static partial void LogTurnStarted(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Agent turn completed for session {SessionId} in {DurationMs}ms. Tool calls: {ToolCalls}")]
    private static partial void LogTurnCompleted(ILogger logger, string sessionId, long durationMs, int toolCalls);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Agent turn failed for session {SessionId} after {DurationMs}ms")]
    private static partial void LogTurnFailed(ILogger logger, Exception ex, string sessionId, long durationMs);

    public async ValueTask<AgentTurnResult> InvokeAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>> next,
        CancellationToken cancellationToken = default)
    {
        var logger = context.Services.GetService<ILogger<LoggingMiddleware>>();
        if (logger is not null)
        {
            LogTurnStarted(logger, context.SessionId);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await next(context, cancellationToken);
            stopwatch.Stop();

            if (logger is not null)
            {
                LogTurnCompleted(
                    logger,
                    context.SessionId,
                    stopwatch.ElapsedMilliseconds,
                    result.Telemetry.ToolCalls);
            }

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            if (logger is not null)
            {
                LogTurnFailed(
                    logger,
                    ex,
                    context.SessionId,
                    stopwatch.ElapsedMilliseconds);
            }
            throw;
        }
    }
}
