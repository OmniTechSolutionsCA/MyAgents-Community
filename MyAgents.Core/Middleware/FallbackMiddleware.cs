using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MyAgents.Core;

/// <summary>
/// Middleware that catches exceptions escaping the inner pipeline and delegates
/// recovery to an <see cref="IFallbackPolicy"/>.
/// Place this at (or near) the outermost edge of the pipeline.
/// </summary>
public sealed partial class FallbackMiddleware : IAgentMiddleware
{
    private static readonly Meter s_meter = new("MyAgents.Core.Fallback", "1.0.0");
    private static readonly Counter<long> s_invoked =
        s_meter.CreateCounter<long>("myagents.fallback.invoked", "{invocations}", "Number of fallback policy invocations.");

    private readonly IFallbackPolicy _policy;
    private readonly ILogger _logger;

    public FallbackMiddleware(IFallbackPolicy policy, ILogger<FallbackMiddleware>? logger = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _logger = logger ?? NullLogger<FallbackMiddleware>.Instance;
    }

    public async ValueTask<AgentTurnResult> InvokeAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>> next,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await next(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFallbackInvoked(_logger, ex, context.SessionId, ex.GetType().Name);
            s_invoked.Add(1, new KeyValuePair<string, object?>("agent.session_id", context.SessionId));
            return await _policy.HandleAsync(ex, context, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "Fallback policy invoked for SessionId={SessionId}. Exception: {ExceptionType}")]
    private static partial void LogFallbackInvoked(ILogger logger, Exception ex, string sessionId, string exceptionType);
}