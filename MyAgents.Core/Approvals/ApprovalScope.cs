namespace MyAgents.Core;

/// <summary>
/// Defines the lifetime and granularity of an approved tool decision.
/// </summary>
public enum ApprovalScope
{
    /// <summary>
    /// Valid only for this single specific invocation (exact call ID or argument hash).
    /// </summary>
    Once = 0,

    /// <summary>
    /// Valid for identical invocations (matching tool name and arguments JSON) within the same session.
    /// </summary>
    SessionCall = 1,

    /// <summary>
    /// Valid for all invocations of this tool within the same session, regardless of arguments.
    /// </summary>
    SessionTool = 2,

    /// <summary>
    /// Valid for all tool invocations within the same session.
    /// </summary>
    SessionAlways = 3,
}