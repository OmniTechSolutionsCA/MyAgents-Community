namespace MyAgents.Core;

/// <summary>
/// Persists normalized execution events for later inspection.
/// </summary>
public interface IRunTraceSink
{
    /// <summary>
    /// Writes a single run-trace record.
    /// </summary>
    ValueTask RecordAsync(RunTraceRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all records for a run ID ordered by write time.
    /// </summary>
    ValueTask<IReadOnlyList<RunTraceRecord>> ListAsync(string runId, CancellationToken cancellationToken = default);
}
