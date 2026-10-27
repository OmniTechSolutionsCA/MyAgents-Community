namespace MyAgents.Core;

/// <summary>
/// Optional query surface for enumerating known run IDs from a trace sink.
/// </summary>
public interface IRunTraceCatalog
{
    /// <summary>
    /// Lists known run IDs ordered by most recently updated first when supported.
    /// </summary>
    ValueTask<IReadOnlyList<string>> ListRunIdsAsync(CancellationToken cancellationToken = default);
}
