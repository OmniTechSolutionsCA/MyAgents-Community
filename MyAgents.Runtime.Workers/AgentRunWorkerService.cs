using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyAgents.Core.Execution;

namespace MyAgents.Runtime.Workers;

internal sealed partial class AgentRunWorkerService : BackgroundService
{
    private readonly IAgentRunBackend _backend;
    private readonly Dictionary<string, IAgentRunHandler> _handlers;
    private readonly AgentRunWorkerOptions _options;
    private readonly ILogger<AgentRunWorkerService> _logger;
    private readonly string _workerId;

    public AgentRunWorkerService(
        IAgentRunBackend backend,
        IEnumerable<IAgentRunHandler> handlers,
        IOptions<AgentRunWorkerOptions> options,
        ILogger<AgentRunWorkerService> logger)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
        ValidateOptions(_options);

        var handlerMap = new Dictionary<string, IAgentRunHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            ArgumentNullException.ThrowIfNull(handler);
            ArgumentException.ThrowIfNullOrWhiteSpace(handler.HandlerId);
            if (!handlerMap.TryAdd(handler.HandlerId, handler))
                throw new InvalidOperationException($"More than one run handler is registered with ID '{handler.HandlerId}'.");
        }

        _handlers = handlerMap;
        _workerId = string.IsNullOrWhiteSpace(_options.WorkerId)
            ? $"{Environment.MachineName}:{Guid.NewGuid():N}"
            : _options.WorkerId;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerLoops = new Task[_options.MaxConcurrentRuns];
        for (var index = 0; index < workerLoops.Length; index++)
            workerLoops[index] = RunWorkerLoopAsync($"{_workerId}:{index}", stoppingToken);

        await Task.WhenAll(workerLoops).ConfigureAwait(false);
    }

    private async Task RunWorkerLoopAsync(string workerId, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var lease = await _backend.ClaimAsync(
                    workerId,
                    _options.LeaseDuration,
                    stoppingToken).ConfigureAwait(false);

                if (lease is null)
                {
                    await Task.Delay(_options.IdlePollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await ExecuteLeaseAsync(lease, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogWorkerLoopFailed(_logger, exception.GetType().Name);
                await Task.Delay(_options.IdlePollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteLeaseAsync(AgentRunLease lease, CancellationToken stoppingToken)
    {
        if (!_handlers.TryGetValue(lease.Request.HandlerId, out var handler))
        {
            await _backend.FailAsync(
                lease,
                new AgentRunFailure("handler_not_found", "No handler is registered for this run.", Retryable: false),
                stoppingToken).ConfigureAwait(false);
            return;
        }

        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (lease.Request.Deadline is { } deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                await _backend.FailAsync(
                    lease,
                    new AgentRunFailure("deadline_exceeded", "The run deadline elapsed before execution.", Retryable: false),
                    stoppingToken).ConfigureAwait(false);
                return;
            }

            executionCancellation.CancelAfter(remaining);
        }

        var stopReason = 0;
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(executionCancellation.Token);
        var heartbeat = MaintainLeaseAsync(lease, executionCancellation, reason =>
        {
            Interlocked.CompareExchange(ref stopReason, (int)reason, 0);
        }, heartbeatCancellation.Token);

        try
        {
            var context = new AgentRunContext(
                lease,
                (checkpoint, cancellationToken) => _backend.SaveCheckpointAsync(
                    lease,
                    checkpoint,
                    cancellationToken));

            var result = await handler.ExecuteAsync(context, executionCancellation.Token).ConfigureAwait(false);
            if (stoppingToken.IsCancellationRequested ||
                (RunWorkerStopReason)Volatile.Read(ref stopReason) == RunWorkerStopReason.LeaseLost)
            {
                return;
            }

            if (executionCancellation.IsCancellationRequested)
            {
                await _backend.CompleteAsync(lease, cancellationToken: stoppingToken).ConfigureAwait(false);
                return;
            }

            if (result is null)
                throw new InvalidOperationException("A run handler returned a null result.");

            switch (result.Disposition)
            {
                case AgentRunHandlerDisposition.Completed:
                    await _backend.CompleteAsync(lease, result.OutputJson, stoppingToken).ConfigureAwait(false);
                    break;
                case AgentRunHandlerDisposition.WaitingForInput:
                    if (result.CheckpointJson is null || string.IsNullOrWhiteSpace(result.PendingInputDescription))
                        throw new InvalidOperationException("A waiting result must include a checkpoint and input description.");
                    await _backend.WaitForInputAsync(
                        lease,
                        result.CheckpointJson,
                        result.PendingInputDescription,
                        stoppingToken).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported run handler disposition '{result.Disposition}'.");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Leave the lease to expire so another worker can recover it after shutdown.
        }
        catch (OperationCanceledException)
        {
            var reason = (RunWorkerStopReason)Volatile.Read(ref stopReason);
            if (reason == RunWorkerStopReason.LeaseLost)
                return;

            if (reason == RunWorkerStopReason.CancellationRequested)
            {
                await _backend.CompleteAsync(lease, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                return;
            }

            await _backend.FailAsync(
                lease,
                new AgentRunFailure("execution_cancelled", "Run execution was cancelled.", Retryable: false),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogRunHandlerFailed(
                _logger,
                lease.Request.HandlerId,
                lease.RunId,
                lease.Attempt,
                exception.GetType().Name);

            var retryDelay = ComputeRetryDelay(lease.Attempt);
            await _backend.RetryAsync(
                lease,
                new AgentRunFailure("handler_failed", "The registered run handler failed; inspect worker diagnostics.", Retryable: true),
                retryDelay,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            heartbeatCancellation.Cancel();
            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the run ends or the worker is stopping.
            }
        }
    }

    private async Task MaintainLeaseAsync(
        AgentRunLease lease,
        CancellationTokenSource executionCancellation,
        Action<RunWorkerStopReason> setStopReason,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_options.LeaseRenewalInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var renewal = await _backend.RenewLeaseAsync(
                    lease,
                    _options.LeaseDuration,
                    cancellationToken).ConfigureAwait(false);

                if (!renewal.LeaseIsValid)
                {
                    setStopReason(RunWorkerStopReason.LeaseLost);
                    executionCancellation.Cancel();
                    return;
                }

                if (renewal.CancellationRequested)
                {
                    setStopReason(RunWorkerStopReason.CancellationRequested);
                    executionCancellation.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the run finishes or the host begins shutdown.
        }
        catch (Exception exception)
        {
            setStopReason(RunWorkerStopReason.LeaseLost);
            executionCancellation.Cancel();
            LogLeaseRenewalFailed(_logger, lease.RunId, exception.GetType().Name);
        }
    }

    private TimeSpan ComputeRetryDelay(int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var ticks = _options.BaseRetryDelay.Ticks;
        if (ticks == 0)
            return TimeSpan.Zero;

        var multiplier = 1L << exponent;
        var delayTicks = ticks > _options.MaximumRetryDelay.Ticks / multiplier
            ? _options.MaximumRetryDelay.Ticks
            : Math.Min(ticks * multiplier, _options.MaximumRetryDelay.Ticks);
        return TimeSpan.FromTicks(delayTicks);
    }

    private static void ValidateOptions(AgentRunWorkerOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConcurrentRuns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.IdlePollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.LeaseDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.LeaseRenewalInterval, TimeSpan.Zero);
        if (options.LeaseRenewalInterval >= options.LeaseDuration)
            throw new ArgumentException("Lease renewal interval must be shorter than the lease duration.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BaseRetryDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumRetryDelay, options.BaseRetryDelay);
    }

    private enum RunWorkerStopReason
    {
        None,
        LeaseLost,
        CancellationRequested,
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "Agent run worker loop failed with {ExceptionType}; polling will resume after the idle delay.")]
    private static partial void LogWorkerLoopFailed(ILogger logger, string exceptionType);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "Run handler {HandlerId} failed for run {RunId} attempt {Attempt} with {ExceptionType}.")]
    private static partial void LogRunHandlerFailed(
        ILogger logger,
        string handlerId,
        string runId,
        int attempt,
        string exceptionType);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Lease renewal failed for run {RunId} with {ExceptionType}; the handler was cancelled.")]
    private static partial void LogLeaseRenewalFailed(ILogger logger, string runId, string exceptionType);
}
