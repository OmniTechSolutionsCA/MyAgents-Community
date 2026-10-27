using MyAgents.Core;
using MyAgents.Core.Memory;

namespace MyAgents.Agents;

/// <summary>
/// Feature that wires an <see cref="IMemoryStore"/> into the agent session.
/// After contributing, the store is available to middleware and tools via
/// <c>context.Services.GetService&lt;IMemoryStore&gt;()</c>.
/// </summary>
/// <remarks>
/// By default, contributes a fresh <see cref="InMemoryMemoryStore"/>.
/// Pass your own store instance to share memory across sessions or use a
/// persistent backend (e.g., Redis, Azure AI Search vector store).
/// </remarks>
/// <example>
/// <code>
/// // In-process memory (default)
/// var agent = new AgentDefinition(
///     Name: "MemoryAgent",
///     Features: new IAgentFeature[]
///     {
///         new MemoryFeature(),          // fresh InMemoryMemoryStore per session
///         new LoggingFeature(),
///     });
///
/// // Shared / persistent store
/// IMemoryStore sharedStore = new MyRedisMemoryStore(...);
/// var agent2 = new AgentDefinition(
///     Name: "SharedMemoryAgent",
///     Features: new IAgentFeature[]
///     {
///         new MemoryFeature(sharedStore),
///         new LoggingFeature(),
///     });
/// </code>
/// </example>
public sealed class MemoryFeature : IAgentFeature
{
    private readonly IMemoryStore? _store;

    /// <summary>
    /// Creates a <see cref="MemoryFeature"/> backed by a fresh <see cref="InMemoryMemoryStore"/>.
    /// </summary>
    public MemoryFeature() { }

    /// <summary>
    /// Creates a <see cref="MemoryFeature"/> backed by the supplied <paramref name="store"/>.
    /// Use this overload to share a store across sessions or inject a persistent backend.
    /// </summary>
    public MemoryFeature(IMemoryStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public void Contribute(AgentAssembly assembly)
    {
        assembly.Memory = _store ?? new InMemoryMemoryStore();
    }
}
