namespace MyAgents.Agents;

/// <summary>
/// Sink for recording model routing and token cost telemetry records.
/// </summary>
public interface ITokenCostTelemetrySink
{
    /// <summary>
    /// Records a model routing and token cost metric record.
    /// </summary>
    ValueTask RecordMetricsAsync(ModelRoutingCostMetrics metrics, CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory accumulator of model routing cost telemetry records.
/// </summary>
public sealed class InMemoryTokenCostTelemetrySink : ITokenCostTelemetrySink
{
    private readonly Lock _gate = new();
    private readonly List<ModelRoutingCostMetrics> _records = [];

    /// <inheritdoc />
    public ValueTask RecordMetricsAsync(ModelRoutingCostMetrics metrics, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        lock (_gate)
        {
            _records.Add(metrics);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Retrieves a snapshot of all recorded metrics.
    /// </summary>
    public IReadOnlyList<ModelRoutingCostMetrics> GetRecords()
    {
        lock (_gate)
        {
            return _records.ToArray();
        }
    }

    /// <summary>
    /// Computes the total estimated cost in USD across all recorded metrics or for a specific session.
    /// </summary>
    public decimal GetTotalCostUsd(string? sessionId = null)
    {
        lock (_gate)
        {
            return _records
                .Where(r => sessionId is null || string.Equals(r.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                .Sum(r => r.EstimatedCostUsd);
        }
    }

    /// <summary>
    /// Computes the total token volume across all recorded metrics or for a specific session.
    /// </summary>
    public long GetTotalTokens(string? sessionId = null)
    {
        lock (_gate)
        {
            return _records
                .Where(r => sessionId is null || string.Equals(r.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                .Sum(r => r.TotalTokens);
        }
    }
}