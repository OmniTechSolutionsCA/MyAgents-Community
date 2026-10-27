namespace MyAgents.Core.Workflows;

/// <summary>
/// Describes a dynamic Send API branch target for <see cref="AgentGraph{TState}"/>.
/// </summary>
/// <typeparam name="TState">Graph state type.</typeparam>
/// <param name="NodeName">Target node to execute.</param>
/// <param name="State">State to pass to that node.</param>
public sealed record SendPacket<TState>(string NodeName, TState State);

/// <summary>
/// Computes runtime parallel branch targets for a graph node.
/// </summary>
/// <typeparam name="TState">Graph state type.</typeparam>
/// <param name="state">Current merged graph state at the split point.</param>
/// <returns>
/// Branch packets to execute concurrently. Return an empty list for no branches.
/// </returns>
public delegate IReadOnlyList<SendPacket<TState>> SendRouter<TState>(TState state);
