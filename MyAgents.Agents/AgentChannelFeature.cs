using MyAgents.Core;
using MyAgents.Core.Messaging;

namespace MyAgents.Agents;

/// <summary>
/// Feature that wires an <see cref="IAgentChannel"/> into the agent's session DI overlay,
/// making it available to tools and middleware via
/// <c>context.Services.GetService&lt;IAgentChannel&gt;()</c>.
/// </summary>
/// <remarks>
/// Create one <see cref="InMemoryAgentChannel"/> and share it across multiple agent definitions
/// so all agents in the group can publish and subscribe to the same bus.
///
/// <code>
/// var bus = new InMemoryAgentChannel();
///
/// var agentA = new AgentDefinition("AgentA",
///     Features: new IAgentFeature[] { new AgentChannelFeature(bus), new LoggingFeature() });
///
/// var agentB = new AgentDefinition("AgentB",
///     Features: new IAgentFeature[] { new AgentChannelFeature(bus), new LoggingFeature() });
/// </code>
/// </remarks>
public sealed class AgentChannelFeature : IAgentFeature
{
    private readonly IAgentChannel _channel;

    public AgentChannelFeature(IAgentChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
    }

    public void Contribute(AgentAssembly assembly)
    {
        // Expose the channel so tools and middleware can resolve it from the turn context.
        assembly.Channel = _channel;

        // Declare the capability so capability-aware routers can route accordingly.
        assembly.Capabilities.Add(AgentCapabilities.Channel);
    }
}
