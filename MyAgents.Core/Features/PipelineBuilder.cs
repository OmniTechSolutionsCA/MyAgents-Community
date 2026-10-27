namespace MyAgents.Core;

/// <summary>
/// Builder for registering middleware during agent assembly.
/// </summary>
public sealed class PipelineBuilder
{
    private readonly List<IAgentMiddleware> _middlewares = new();

    /// <summary>
    /// Adds middleware to the pipeline. Middleware executes in registration order.
    /// </summary>
    /// <param name="middleware">The middleware to add.</param>
    /// <returns>The builder for fluent chaining.</returns>
    public PipelineBuilder Use(IAgentMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        _middlewares.Add(middleware);
        return this;
    }

    /// <summary>
    /// Builds the middleware pipeline.
    /// </summary>
    /// <returns>The built pipeline ready for execution.</returns>
    public AgentPipeline Build() => new(_middlewares);
}
