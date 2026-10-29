using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MyAgents.Core;

/// <summary>
/// Configuration options for <see cref="ResilienceMiddleware"/>.
/// </summary>
public sealed class ResilienceOptions
{
    /// <summary>Maximum number of retry attempts for transient failures. Default: 2.</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Base delay for exponential backoff. Default: 200ms.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);
}

/// <summary>
/// Middleware that provides retry resilience with exponential backoff and jitter.
/// This should be placed at the outermost edge of the pipeline.
/// </summary>
public sealed partial class ResilienceMiddleware : IAgentMiddleware
{
    private static readonly Meter s_meter = new("MyAgents.Core.Resilience", "1.0.0");
    private static readonly Counter<long> s_retryCount =
        s_meter.CreateCounter<long>("myagents.resilience.retries", "{retries}", "Number of retry attempts.");
    private static readonly Counter<long> s_retryExhausted =
        s_meter.CreateCounter<long>("myagents.resilience.exhausted", "{failures}", "Number of retry-exhausted failures.");

    private readonly ITransientErrorClassifier _classifier;
    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;
    private readonly ILogger _logger;

    public ResilienceMiddleware(
        ITransientErrorClassifier? classifier = null,
        int maxRetries = 2,
        TimeSpan? baseDelay = null,
        ILogger<ResilienceMiddleware>? logger = null)
    {
        _classifier = classifier ?? new DefaultTransientErrorClassifier();
        _maxRetries = maxRetries < 0 ? throw new ArgumentOutOfRangeException(nameof(maxRetries)) : maxRetries;
        _baseDelay = baseDelay ?? TimeSpan.FromMilliseconds(200);
        _logger = logger ?? NullLogger<ResilienceMiddleware>.Instance;
    }

    /// <summary>
    /// Creates a <see cref="ResilienceMiddleware"/> from <see cref="IOptions{ResilienceOptions}"/>.
    /// </summary>
    public ResilienceMiddleware(
        IOptions<ResilienceOptions> options,
        ITransientErrorClassifier? classifier = null,
        ILogger<ResilienceMiddleware>? logger = null)
        : this(classifier, options?.Value?.MaxRetries ?? 2, options?.Value?.BaseDelay, logger) { }

    public async ValueTask<AgentTurnResult> InvokeAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>> next,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < _maxRetries && _classifier.IsTransient(ex))
            {
                var jitterMs = Random.Shared.Next(0, 100);
                var delay = TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * Math.Pow(2, attempt) + jitterMs);
                LogTransientError(_logger, ex, attempt + 1, _maxRetries, delay.TotalMilliseconds, context.SessionId);
                s_retryCount.Add(1, new KeyValuePair<string, object?>("agent.session_id", context.SessionId));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt >= _maxRetries && _classifier.IsTransient(ex))
            {
                LogRetryExhausted(_logger, ex, _maxRetries, context.SessionId);
                s_retryExhausted.Add(1, new KeyValuePair<string, object?>("agent.session_id", context.SessionId));
                throw;
            }
        }
    }

    /// <summary>
    /// Streaming variant: if the stream faults with a transient error <em>before</em>
    /// any chunk has been yielded to the caller, the stream is restarted from scratch
    /// (up to <see cref="_maxRetries"/> times with exponential back-off).
    /// Once at least one chunk has been forwarded a retry is suppressed — restarting
    /// mid-stream would produce duplicate or confusing output for the caller.
    /// </summary>
    /// <remarks>
    /// The enumerator is driven manually (rather than with <c>await foreach</c>) so that
    /// <c>MoveNextAsync</c> can be called inside a <see langword="try"/>/<see langword="catch"/>
    /// block, while <c>yield return</c> sits outside any catch clause — a requirement
    /// imposed by the C# compiler for async iterator methods.
    /// </remarks>
    public async IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, IAsyncEnumerable<AgentTurnResult>> next,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var yieldedAny = false;
            var needsRetry = false;

            // 'await using' compiles to try/finally — yield return inside try/finally
            // (without a catch clause) is valid in C# 8+.
            var enumerator = next(context, cancellationToken).GetAsyncEnumerator(cancellationToken);
            await using (enumerator.ConfigureAwait(false))
            {
                while (true)
                {
                    AgentTurnResult current;
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                            break; // stream finished normally

                        current = enumerator.Current;
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // never swallow cancellation
                    }
                    catch (Exception ex) when (!yieldedAny && attempt < _maxRetries && _classifier.IsTransient(ex))
                    {
                        LogStreamingTransientError(_logger, ex, attempt + 1, _maxRetries, context.SessionId);
                        s_retryCount.Add(1, new KeyValuePair<string, object?>("agent.session_id", context.SessionId));
                        needsRetry = true;
                        break;
                    }

                    // yield return is intentionally outside any try/catch.
                    yieldedAny = true;
                    yield return current;
                }
            }

            if (!needsRetry)
                yield break; // normal completion or non-retryable error

            var jitterMs = Random.Shared.Next(0, 100);
            var delay = TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * Math.Pow(2, attempt) + jitterMs);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Transient error on attempt {Attempt}/{MaxRetries}. Retrying in {DelayMs}ms. SessionId={SessionId}")]
    private static partial void LogTransientError(ILogger logger, Exception ex, int attempt, int maxRetries, double delayMs, string sessionId);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "Retry exhausted after {MaxRetries} attempts. SessionId={SessionId}")]
    private static partial void LogRetryExhausted(ILogger logger, Exception ex, int maxRetries, string sessionId);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Transient error during streaming on attempt {Attempt}/{MaxRetries}. SessionId={SessionId}")]
    private static partial void LogStreamingTransientError(ILogger logger, Exception ex, int attempt, int maxRetries, string sessionId);
}