using MyAgents.Core;

namespace MyAgents.Agents;

/// <summary>
/// Feature that wires a <see cref="FallbackMiddleware"/> into the agent pipeline.
/// </summary>
/// <remarks>
/// Place this at the outermost position in the features list so the fallback
/// wraps the entire pipeline (including <see cref="ResilienceFeature"/> when present):
/// <code>
/// var agent = new AgentDefinition(
///     Name: "ResilientAgent",
///     Features: new IAgentFeature[]
///     {
///         new FallbackFeature(new StaticFallbackPolicy()),   // outermost — catches everything
///         new ResilienceFeature(maxRetries: 3),              // retries before fallback fires
///         new LoggingFeature(),
///     });
/// </code>
///
/// For dead-letter capture, use <see cref="DeadLetterFallbackPolicy"/>:
/// <code>
/// var dlq = new InMemoryDeadLetterQueue();
/// var agent = new AgentDefinition(
///     Name: "DeadLetterAgent",
///     Features: new IAgentFeature[]
///     {
///         new FallbackFeature(new DeadLetterFallbackPolicy(dlq)),
///         new ResilienceFeature(),
///         new LoggingFeature(),
///     });
/// // Later: inspect dlq.Items for replay / alerting
/// </code>
/// </remarks>
public sealed class FallbackFeature : IAgentFeature
{
    private readonly IFallbackPolicy _policy;

    /// <param name="policy">The fallback policy to invoke when unhandled exceptions escape the pipeline.</param>
    public FallbackFeature(IFallbackPolicy policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    /// <inheritdoc />
    public void Contribute(AgentAssembly assembly)
    {
        assembly.Pipeline.Use(new FallbackMiddleware(_policy));
    }
}
