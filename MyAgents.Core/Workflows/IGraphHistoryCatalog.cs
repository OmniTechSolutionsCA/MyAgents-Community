namespace MyAgents.Core.Workflows;

/// <summary>
/// Optional query surface for enumerating graph-history sessions.
/// </summary>
public interface IGraphHistoryCatalog
{
    /// <summary>
    /// Lists known graph session IDs ordered by most recently updated first when supported.
    /// </summary>
    ValueTask<IReadOnlyList<string>> ListSessionIdsAsync(CancellationToken cancellationToken = default);
}
