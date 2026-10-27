using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MyAgents.Core;

/// <summary>
/// Configuration options for <see cref="CircuitBreakerMiddleware"/>.
/// </summary>
public sealed class CircuitBreakerOptions
{
    /// <summary>Consecutive transient failures before opening the circuit. Default: 5.</summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>How long the circuit stays open before probing. Default: 30 seconds.</summary>
    public TimeSpan OpenDuration { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Represents the current state of a circuit breaker.
/// </summary>
public enum CircuitState
{
    /// <summary>Normal operation. Failures are counted toward the threshold.</summary>
    Closed,

    /// <summary>
    /// The failure threshold was reached. All calls are fast-failed with
    /// <see cref="CircuitBreakerOpenException"/> until the open duration elapses.
    /// </summary>
    Open,

    /// <summary>
    /// The open duration has elapsed. One probe call is allowed through;
    /// success closes the circuit, failure re-opens it.
    /// </summary>
    HalfOpen
}

/// <summary>
/// Thrown when a call is attempted against a circuit that is in the
/// <see cref="CircuitState.Open"/> state.
/// </summary>
public sealed class CircuitBreakerOpenException : Exception
{
    /// <inheritdoc />
    public CircuitBreakerOpenException()
        : base("The circuit breaker is open; calls are temporarily suspended.") { }

    /// <inheritdoc />
    public CircuitBreakerOpenException(string message) : base(message) { }
}

/// <summary>
/// Middleware that implements the circuit-breaker pattern to prevent cascading
/// failures when a downstream dependency is unhealthy.
/// </summary>
/// <remarks>
/// <para>
/// State transitions:
/// <list type="bullet">
///   <item><description><b>Closed → Open</b>: <see cref="failureThreshold"/> consecutive
///     transient failures (as classified by the <see cref="ITransientErrorClassifier"/>).</description></item>
///   <item><description><b>Open → HalfOpen</b>: <paramref name="openDuration"/> has elapsed.</description></item>
///   <item><description><b>HalfOpen → Closed</b>: the probe call succeeds.</description></item>
///   <item><description><b>HalfOpen → Open</b>: the probe call fails.</description></item>
/// </list>
/// </para>
/// <para>
/// This middleware is thread-safe. Compose it at the outermost position in the pipeline,
/// before <see cref="ResilienceMiddleware"/>, so retries are not attempted when the circuit
/// is open.
/// </para>
/// </remarks>
public sealed class CircuitBreakerMiddleware : IAgentMiddleware
{
    private static readonly Meter s_meter = new("MyAgents.Core.CircuitBreaker", "1.0.0");
    private static readonly Counter<long> s_stateChanges =
        s_meter.CreateCounter<long>("myagents.circuit_breaker.state_changes", "{changes}", "Circuit breaker state transitions.");
    private static readonly Counter<long> s_rejected =
        s_meter.CreateCounter<long>("myagents.circuit_breaker.rejected", "{rejections}", "Calls rejected by open circuit.");

    private readonly ITransientErrorClassifier _classifier;
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly ILogger _logger;

    private CircuitState _state = CircuitState.Closed;
    private int _consecutiveFailures;
    private DateTimeOffset _openedAt;
    private readonly object _lock = new();

    /// <param name="failureThreshold">
    /// Number of consecutive transient failures that trips the circuit. Default: 5.
    /// </param>
    /// <param name="openDuration">
    /// How long the circuit stays open before transitioning to
    /// <see cref="CircuitState.HalfOpen"/> for a probe. Default: 30 seconds.
    /// </param>
    /// <param name="classifier">
    /// Determines which exceptions count as circuit-tripping failures.
    /// Defaults to <see cref="DefaultTransientErrorClassifier"/>.
    /// </param>
    /// <param name="logger">Optional logger. Defaults to <see cref="NullLogger"/>.</param>
    public CircuitBreakerMiddleware(
        int failureThreshold = 5,
        TimeSpan? openDuration = null,
        ITransientErrorClassifier? classifier = null,
        ILogger<CircuitBreakerMiddleware>? logger = null)
    {
        if (failureThreshold < 1)
            throw new ArgumentOutOfRangeException(nameof(failureThreshold), "Must be at least 1.");

        _failureThreshold = failureThreshold;
        _openDuration = openDuration ?? TimeSpan.FromSeconds(30);
        _classifier = classifier ?? new DefaultTransientErrorClassifier();
        _logger = logger ?? NullLogger<CircuitBreakerMiddleware>.Instance;
    }

    /// <summary>
    /// Creates a <see cref="CircuitBreakerMiddleware"/> from <see cref="IOptions{CircuitBreakerOptions}"/>.
    /// </summary>
    public CircuitBreakerMiddleware(
        IOptions<CircuitBreakerOptions> options,
        ITransientErrorClassifier? classifier = null,
        ILogger<CircuitBreakerMiddleware>? logger = null)
        : this(options?.Value?.FailureThreshold ?? 5, options?.Value?.OpenDuration, classifier, logger) { }

    /// <summary>Returns the current (effective) state of the circuit.</summary>
    public CircuitState State
    {
        get { lock (_lock) return GetEffectiveState(); }
    }

    /// <summary>
    /// Manually resets the circuit to <see cref="CircuitState.Closed"/>.
    /// Useful in tests or operator-driven recovery scenarios.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _state = CircuitState.Closed;
            _consecutiveFailures = 0;
        }
    }

    public async ValueTask<AgentTurnResult> InvokeAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, ValueTask<AgentTurnResult>> next,
        CancellationToken cancellationToken = default)
    {
        ThrowIfOpen();

        try
        {
            var result = await next(context, cancellationToken).ConfigureAwait(false);
            OnSuccess();
            return result;
        }
        catch (Exception ex) when (!IsCancellation(ex) && _classifier.IsTransient(ex))
        {
            OnFailure();
            throw;
        }
    }

    /// <summary>
    /// Streaming variant: each yielded chunk passes through normally.
    /// The circuit is updated once the stream completes or a transient error
    /// aborts enumeration.
    /// </summary>
    public async IAsyncEnumerable<AgentTurnResult> InvokeStreamingAsync(
        AgentTurnContext context,
        Func<AgentTurnContext, CancellationToken, IAsyncEnumerable<AgentTurnResult>> next,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfOpen();

        // Track whether iteration completed successfully.
        // The try/finally (no catch) is required because 'yield return' inside
        // a try/catch is not permitted by the C# compiler.
        var succeeded = false;
        try
        {
            await foreach (var chunk in next(context, cancellationToken).ConfigureAwait(false))
                yield return chunk;

            succeeded = true;
        }
        finally
        {
            if (succeeded)
                OnSuccess();
            else
                OnFailure();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void ThrowIfOpen()
    {
        lock (_lock)
        {
            if (GetEffectiveState() == CircuitState.Open)
            {
                _logger.LogWarning("Circuit breaker is open. Rejecting call.");
                s_rejected.Add(1);
                throw new CircuitBreakerOpenException();
            }
        }
    }

    private void OnSuccess()
    {
        lock (_lock)
        {
            var previous = _state;
            _consecutiveFailures = 0;
            _state = CircuitState.Closed;
            if (previous != CircuitState.Closed)
            {
                _logger.LogInformation("Circuit breaker transitioned {From} -> Closed.", previous);
                s_stateChanges.Add(1,
                    new KeyValuePair<string, object?>("from", previous.ToString()),
                    new KeyValuePair<string, object?>("to", "Closed"));
            }
        }
    }

    private void OnFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _failureThreshold)
            {
                var previous = _state;
                _state = CircuitState.Open;
                _openedAt = DateTimeOffset.UtcNow;
                _logger.LogError(
                    "Circuit breaker opened after {Failures} consecutive failures. Was {From}.",
                    _consecutiveFailures, previous);
                s_stateChanges.Add(1,
                    new KeyValuePair<string, object?>("from", previous.ToString()),
                    new KeyValuePair<string, object?>("to", "Open"));
            }
        }
    }

    /// <summary>
    /// Transitions Open → HalfOpen once the open duration has elapsed.
    /// Must be called inside <see cref="_lock"/>.
    /// </summary>
    private CircuitState GetEffectiveState()
    {
        if (_state == CircuitState.Open &&
            DateTimeOffset.UtcNow - _openedAt >= _openDuration)
        {
            _logger.LogInformation("Circuit breaker transitioned Open -> HalfOpen after {Duration}.", _openDuration);
            s_stateChanges.Add(1,
                new KeyValuePair<string, object?>("from", "Open"),
                new KeyValuePair<string, object?>("to", "HalfOpen"));
            _state = CircuitState.HalfOpen;
        }
        return _state;
    }

    private static bool IsCancellation(Exception ex) => ex is OperationCanceledException;
}
