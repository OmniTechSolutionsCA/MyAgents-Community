namespace MyAgents.Core;

/// <summary>
/// A feature that contributes capabilities to an agent.
/// Features are composed, not inherited.
/// </summary>
public interface IAgentFeature
{
    /// <summary>
    /// Synchronously contributes to the agent assembly (tools, middleware, prompts).
    /// Prefer overriding <see cref="ContributeAsync"/> when the feature needs to perform
    /// async I/O (e.g. connecting to a remote server). The default implementation of
    /// <see cref="ContributeAsync"/> delegates here, so features that have no async
    /// work only need to implement this method.
    /// </summary>
    /// <param name="assembly">The assembly to contribute to.</param>
    void Contribute(AgentAssembly assembly);

    /// <summary>
    /// Asynchronously contributes to the agent assembly.
    /// Override this method when the feature must perform async I/O during assembly
    /// (e.g. connecting to an MCP server, loading a prompt from a remote store).
    /// The default implementation calls the synchronous <see cref="Contribute"/> on the
    /// current thread and completes immediately — no sync-over-async risk for features
    /// that only implement the synchronous path.
    /// </summary>
    /// <param name="assembly">The assembly to contribute to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask ContributeAsync(AgentAssembly assembly, CancellationToken cancellationToken = default)
    {
        Contribute(assembly);
        return ValueTask.CompletedTask;
    }
}
