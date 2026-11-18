namespace MyAgents.Core.Security;

/// <summary>
/// Identifies the outbound payload an egress scan examined.
/// </summary>
public enum EgressChannel
{
    /// <summary>The assistant's own output text for the turn. Scanned after the turn, before it is yielded.</summary>
    TurnOutput = 0,

    /// <summary>
    /// The serialised JSON arguments of a tool call. Scanned before the tool is invoked, so a
    /// violation prevents the call from reaching a network, MCP server, or subagent.
    /// </summary>
    ToolArguments = 1,
}
