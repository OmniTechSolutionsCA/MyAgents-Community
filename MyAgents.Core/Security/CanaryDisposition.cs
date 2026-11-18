namespace MyAgents.Core.Security;

/// <summary>
/// What a <see cref="ICanaryViolationHandler"/> decides to do about a violation it was given.
/// </summary>
public enum CanaryDisposition
{
    /// <summary>Let the turn (or the tool call) proceed. Used for audit-only rollouts.</summary>
    Allow = 0,

    /// <summary>Stop the turn or the tool call and fail closed.</summary>
    Abort = 1,
}
