using MyAgents.Core;

namespace MyAgents.Agents;

/// <summary>
/// Enables framework-level function calling for runtimes that return tool-call
/// envelopes as assistant messages instead of invoking tools natively.
/// </summary>
public sealed class FunctionCallingFeature : IAgentFeature
{
    private readonly FunctionCallingMiddleware _middleware;

    public FunctionCallingFeature(int maxToolRoundTrips = 8)
    {
        _middleware = new FunctionCallingMiddleware(maxToolRoundTrips);
    }

    public void Contribute(AgentAssembly assembly)
    {
        assembly.Pipeline.Use(_middleware);
        assembly.Capabilities.Add(AgentCapabilities.Tools);
    }
}