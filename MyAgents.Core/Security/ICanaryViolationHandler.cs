namespace MyAgents.Core.Security;

/// <summary>
/// Decides the reaction to an egress violation.
/// </summary>
/// <remarks>
/// An implementation is registered once per agent definition and may be invoked from several
/// sessions concurrently, so it must be thread-safe and hold no per-session state.
/// </remarks>
public interface ICanaryViolationHandler
{
    /// <summary>
    /// Decides what to do about <paramref name="context"/>.
    /// </summary>
    /// <param name="context">The violation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The disposition the framework should apply.</returns>
    ValueTask<CanaryDisposition> HandleAsync(
        CanaryViolationContext context,
        CancellationToken cancellationToken = default);
}
