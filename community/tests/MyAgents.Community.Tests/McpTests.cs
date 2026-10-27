using System.Text.Json;
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
