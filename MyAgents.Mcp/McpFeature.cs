using ModelContextProtocol.Client;
using MyAgents.Core;
using MyAgents.Core.Prompts;

namespace MyAgents.Mcp;

/// <summary>
/// Additive MCP feature options. Defaults preserve the original tool-only behavior.
/// </summary>
public sealed record McpFeatureOptions(
    bool ImportPrompts = false,
    bool ExposeResourceTools = false);

/// <summary>
/// Agent feature that connects to an MCP server and contributes all its tools
/// to the agent assembly. Supports both stdio and HTTP transports via
/// <see cref="IClientTransport"/>.
/// </summary>
/// <remarks>
/// The underlying <see cref="McpClient"/> holds live resources (a child process
/// for stdio, or an HTTP connection for HTTP transport). The feature registers
/// that client as a session-owned resource so it is released when the agent
/// session ends.
/// </remarks>
public sealed class McpFeature : IAgentFeature
{
    private readonly IClientTransport _transport;
    private readonly McpFeatureOptions _options;

    /// <summary>
    /// Creates an <see cref="McpFeature"/> that will connect to an MCP server
    /// via the supplied transport.
    /// </summary>
    /// <param name="transport">
    /// Use <see cref="StdioClientTransport"/> for local process servers, or
    /// <see cref="HttpClientTransport"/> for remote HTTP / SSE servers.
    /// </param>
    public McpFeature(IClientTransport transport)
        : this(transport, options: null)
    {
    }

    /// <summary>
    /// Creates an <see cref="McpFeature"/> with additive prompt/resource options.
    /// </summary>
    public McpFeature(IClientTransport transport, McpFeatureOptions? options)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new McpFeatureOptions();
    }

    /// <summary>
    /// Synchronous contribution entry point required by <see cref="IAgentFeature"/>.
    /// Delegates to <see cref="ContributeAsync"/> — prefer calling
    /// <see cref="ContributeAsync"/> directly (via <see cref="DefaultAgentSessionFactory"/>)
    /// to avoid blocking a thread-pool thread.
    /// </summary>
    public void Contribute(AgentAssembly assembly)
        => ContributeAsync(assembly).AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Asynchronously connects to the MCP server, discovers all tools, and registers
    /// each as an <see cref="MyAgents.Core.Tools.ITool"/> on the agent assembly.
    /// </summary>
    public async ValueTask ContributeAsync(
        AgentAssembly assembly,
        CancellationToken cancellationToken = default)
    {
        var client = await McpClient.CreateAsync(
            _transport,
            clientOptions: null,
            loggerFactory: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        assembly.RegisterOwnedResource(client);

        IList<McpClientTool> tools = await client.ListToolsAsync(
            options: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var descriptors = tools
            .Select(static tool => new McpToolDescriptor(tool.Name, tool.Description))
            .ToArray();

        var sessionContext = new McpSessionContext(
            [
                new McpServerRegistration(
                    Prefix: null,
                    DisplayName: "default",
                    Client: client,
                    Tools: descriptors),
            ]);

        foreach (McpClientTool mcpTool in tools)
        {
            assembly.Tools.Add(new McpToolAdapter(client, mcpTool));
        }

        assembly.RegisterSessionService<IMcpSessionContext>(sessionContext);

        if (_options.ImportPrompts)
        {
            assembly.PromptRegistry ??= new PromptRegistry();
            await sessionContext.ImportPromptsAsync(assembly.PromptRegistry, cancellationToken).ConfigureAwait(false);
        }

        if (_options.ExposeResourceTools)
        {
            assembly.Tools.Add(new McpListResourcesTool(sessionContext));
            assembly.Tools.Add(new McpReadResourceTool(sessionContext));
        }
    }
}
