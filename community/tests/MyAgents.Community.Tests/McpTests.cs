using System.IO.Pipelines;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using MyAgents.Core;
using MyAgents.Core.Tools;
using MyAgents.Mcp;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class McpTests
{
    private sealed class SimpleTool(string name) : ITool
    {
        public string Name => name;
        public string? Description => $"Tool {name}";
        public string? ParametersSchemaJson => null;

        public ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult($"result-from-{name}");
        }
    }

    [Test]
    public void DynamicToolCatalog_registers_static_and_dynamic_tools_and_fires_events()
    {
        var catalog = new DynamicToolCatalog([new SimpleTool("base_tool")]);
        var eventsCount = 0;
        catalog.CatalogChanged += (_, _) => eventsCount++;

        Assert.That(catalog.Tools, Has.Count.EqualTo(1));
        Assert.That(catalog.TryGet("base_tool", out var tool1), Is.True);
        Assert.That(tool1.Name, Is.EqualTo("base_tool"));

        // Add dynamic tools for server "sqlite-mcp"
        catalog.SetDynamicTools("sqlite-mcp", [new SimpleTool("sqlite_query"), new SimpleTool("sqlite_schema")]);

        Assert.Multiple(() =>
        {
            Assert.That(eventsCount, Is.EqualTo(1));
            Assert.That(catalog.Tools, Has.Count.EqualTo(3));
            Assert.That(catalog.TryGet("sqlite_query", out _), Is.True);
            Assert.That(catalog.TryGet("sqlite_schema", out _), Is.True);
        });

        // Remove dynamic tools
        var removed = catalog.RemoveDynamicTools("sqlite-mcp");
        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.True);
            Assert.That(eventsCount, Is.EqualTo(2));
            Assert.That(catalog.Tools, Has.Count.EqualTo(1));
            Assert.That(catalog.TryGet("sqlite_query", out _), Is.False);
        });
    }

    [Test]
    public async Task McpSkillDiscoveryService_discovers_valid_skills_and_ignores_malformed_json()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "myagents_skills_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var validSkill = new
            {
                name = "code-review",
                description = "Automated code reviewer skill",
                tags = new[] { "review", "git" }
            };
            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "review.json"),
                JsonSerializer.Serialize(validSkill));

            // Malformed JSON file
            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "invalid.json"),
                "{ not valid json syntax ]]]");

            // Valid JSON but without Name
            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "other.json"),
                "{\"foo\": \"bar\"}");

            var discoveryService = new McpSkillDiscoveryService();
            var discovered = await discoveryService.DiscoverSkillsAsync(tempDir);

            Assert.Multiple(() =>
            {
                Assert.That(discovered, Has.Count.EqualTo(1));
                Assert.That(discovered[0].Name, Is.EqualTo("code-review"));
                Assert.That(discovered[0].Description, Is.EqualTo("Automated code reviewer skill"));
                Assert.That(discovered[0].Tags, Does.Contain("review"));
            });
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    /// <summary>
    /// A real MCP server running in-process over stream pipes, so <see cref="McpFeature"/> speaks the
    /// actual wire protocol without any network or external process.
    /// </summary>
    private sealed class InProcessMcpServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Pipe _clientToServer = new();
        private readonly Pipe _serverToClient = new();
        private readonly Task _running;

        public InProcessMcpServer(params string[] toolNames)
        {
            var tools = new McpServerPrimitiveCollection<McpServerTool>();
            foreach (var name in toolNames)
            {
                tools.Add(McpServerTool.Create(
                    (string message) => $"{name}: {message}",
                    new McpServerToolCreateOptions { Name = name }));
            }

            var options = new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "in-process-test-server", Version = "1.0.0" },
                ToolCollection = tools,
            };

            _running = RunAsync(options);
        }

        public IClientTransport ClientTransport => new StreamClientTransport(
            _clientToServer.Writer.AsStream(),
            _serverToClient.Reader.AsStream());

        private async Task RunAsync(McpServerOptions options)
        {
            await using var server = McpServer.Create(
                new StreamServerTransport(_clientToServer.Reader.AsStream(), _serverToClient.Writer.AsStream()),
                options);
            await server.RunAsync(_stop.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _clientToServer.Writer.Complete();
            _serverToClient.Writer.Complete();
            try
            {
                await _running;
            }
            catch (OperationCanceledException)
            {
                // Expected when the server is stopped.
            }
            _stop.Dispose();
        }
    }

    [Test]
    public async Task McpFeature_exposes_only_allowed_tools_when_filter_is_set()
    {
        await using var server = new InProcessMcpServer("echo", "secret-tool");
        var feature = new McpFeature(
            server.ClientTransport,
            new McpFeatureOptions(AllowedToolNames: new HashSet<string> { "echo" }));
        var assembly = new AgentAssembly();

        await feature.ContributeAsync(assembly);

        Assert.That(assembly.Tools.Build().Select(t => t.Name).ToArray(), Is.EqualTo(new[] { "echo" }));
    }

    [Test]
    public async Task McpFeature_exposes_all_tools_when_no_filter_is_set()
    {
        await using var server = new InProcessMcpServer("echo", "secret-tool");
        var feature = new McpFeature(server.ClientTransport);
        var assembly = new AgentAssembly();

        await feature.ContributeAsync(assembly);

        Assert.That(
            assembly.Tools.Build().Select(t => t.Name).ToArray(),
            Is.EquivalentTo(new[] { "echo", "secret-tool" }));
    }

    [Test]
    public async Task McpServerHotReloadManager_returns_error_result_when_reloading_unregistered_server()
    {
        var catalog = new DynamicToolCatalog();
        var manager = new McpServerHotReloadManager(catalog);

        var result = await manager.ReloadServerAsync("nonexistent-server");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("not registered"));
        });
    }
}
