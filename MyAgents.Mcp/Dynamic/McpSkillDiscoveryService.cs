using System.Text.Json;

namespace MyAgents.Mcp;

/// <summary>
/// Discovered skill metadata located dynamically from file systems or manifests.
/// </summary>
public sealed record DiscoveredSkill(
    string Name,
    string Description,
    string SourcePath,
    IReadOnlyList<string> Tags);

/// <summary>
/// Service that dynamically discovers skill packs and tool specifications from local storage or manifests.
/// </summary>
public interface ISkillDiscoveryService
{
    /// <summary>
    /// Scans the target directory for skill manifests and tool definitions.
    /// </summary>
    ValueTask<IReadOnlyList<DiscoveredSkill>> DiscoverSkillsAsync(string directoryPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default file-backed implementation of <see cref="ISkillDiscoveryService"/>.
/// </summary>
public sealed class McpSkillDiscoveryService : ISkillDiscoveryService
{
    private sealed record SkillManifest(string? Name, string? Description, string[]? Tags);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<DiscoveredSkill>> DiscoverSkillsAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        if (!Directory.Exists(directoryPath))
        {
            return Array.Empty<DiscoveredSkill>();
        }

        var results = new List<DiscoveredSkill>();
        var manifestFiles = Directory.GetFiles(directoryPath, "*.json", SearchOption.AllDirectories);

        foreach (var file in manifestFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                var manifest = JsonSerializer.Deserialize<SkillManifest>(content, McpJsonOptions.CaseInsensitive);

                if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.Name))
                {
                    results.Add(new DiscoveredSkill(
                        Name: manifest.Name,
                        Description: manifest.Description ?? string.Empty,
                        SourcePath: file,
                        Tags: manifest.Tags ?? Array.Empty<string>()));
                }
            }
            catch
            {
                // Skip invalid or non-manifest json files
            }
        }

        return results;
    }
}