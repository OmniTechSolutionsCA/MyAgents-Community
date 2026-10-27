using System.Collections.Concurrent;
using System.Text.Json;

namespace MyAgents.Core.Workflows;

/// <summary>
/// <see cref=""IGraphHistoryStore""/> implementation that persists <see cref=""GraphSnapshot""/> records
/// as JSON files on the local file system.
/// </summary>
public sealed class FileSystemGraphHistoryStore : IGraphHistoryStore, IGraphHistoryCatalog
{
    private static readonly JsonSerializerOptions s_jsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _directory;
    private readonly ConcurrentDictionary<string, string> _snapshotToSessionIndex = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a new <see cref=""FileSystemGraphHistoryStore""/> using <paramref name=""directory""/>.
    /// </summary>
    public FileSystemGraphHistoryStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    /// <inheritdoc/>
    public async ValueTask SaveSnapshotAsync(GraphSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var sessionDir = Path.Combine(_directory, SanitizeFileName(snapshot.SessionId));
        Directory.CreateDirectory(sessionDir);

        var safeSnapshotId = SanitizeFileName(snapshot.SnapshotId);
        var fileName = $"{snapshot.StateVersion:D6}_{safeSnapshotId}.json";
        var filePath = Path.Combine(sessionDir, fileName);

        var json = JsonSerializer.Serialize(snapshot, s_jsonOptions);
        await File.WriteAllTextAsync(filePath, json, ct).ConfigureAwait(false);

        _snapshotToSessionIndex[snapshot.SnapshotId] = snapshot.SessionId;
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<GraphSnapshot>> ListSnapshotsAsync(
        string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var sessionDir = Path.Combine(_directory, SanitizeFileName(sessionId));
        if (!Directory.Exists(sessionDir))
            return Array.Empty<GraphSnapshot>();

        var files = Directory.GetFiles(sessionDir, "*.json")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        var snapshots = new List<GraphSnapshot>(files.Length);
        foreach (var file in files)
        {
            var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            var snapshot = JsonSerializer.Deserialize<GraphSnapshot>(json, s_jsonOptions);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
                _snapshotToSessionIndex[snapshot.SnapshotId] = snapshot.SessionId;
            }
        }

        return snapshots.OrderBy(static s => s.StateVersion).ToArray();
    }

    /// <inheritdoc/>
    public async ValueTask<GraphSnapshot?> GetSnapshotAsync(
        string snapshotId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        if (_snapshotToSessionIndex.TryGetValue(snapshotId, out var sessionId))
        {
            var sessionDir = Path.Combine(_directory, SanitizeFileName(sessionId));
            if (Directory.Exists(sessionDir))
            {
                var match = Directory.GetFiles(sessionDir, $"*_{SanitizeFileName(snapshotId)}.json").FirstOrDefault();
                if (match is not null && File.Exists(match))
                {
                    var json = await File.ReadAllTextAsync(match, ct).ConfigureAwait(false);
                    return JsonSerializer.Deserialize<GraphSnapshot>(json, s_jsonOptions);
                }
            }
        }

        // Fallback scan across session directories
        if (!Directory.Exists(_directory))
            return null;

        var safeId = SanitizeFileName(snapshotId);
        foreach (var sessionDir in Directory.GetDirectories(_directory))
        {
            var match = Directory.GetFiles(sessionDir, $"*_{safeId}.json").FirstOrDefault();
            if (match is not null && File.Exists(match))
            {
                var json = await File.ReadAllTextAsync(match, ct).ConfigureAwait(false);
                var snapshot = JsonSerializer.Deserialize<GraphSnapshot>(json, s_jsonOptions);
                if (snapshot is not null)
                {
                    _snapshotToSessionIndex[snapshot.SnapshotId] = snapshot.SessionId;
                    return snapshot;
                }
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> ListSessionIdsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directory))
            return ValueTask.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var dirs = new DirectoryInfo(_directory)
            .GetDirectories()
            .OrderByDescending(static d => d.LastWriteTimeUtc)
            .Select(static d => d.Name)
            .ToArray();

        return ValueTask.FromResult<IReadOnlyList<string>>(dirs);
    }

    private static string SanitizeFileName(string name)
    {
        var invalids = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalids.Contains(c) ? '_' : c));
    }
}