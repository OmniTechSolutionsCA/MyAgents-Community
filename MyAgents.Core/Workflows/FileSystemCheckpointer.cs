using System.Text.Json;

namespace MyAgents.Core.Workflows;

/// <summary>
/// <see cref="IWorkflowCheckpointer"/> implementation that persists each
/// <see cref="WorkflowSnapshot"/> as a JSON file on the local file system.
/// </summary>
/// <remarks>
/// <para>File naming: <c>{directory}/{sessionId}.checkpoint.json</c>.</para>
/// <para>Suitable for development, testing, and single-node deployments.
/// For distributed or production scenarios use a database-backed implementation.</para>
/// <para>Thread-safety: concurrent reads are safe. Concurrent writes to the
/// <em>same</em> session id are serialized via a per-session lock.</para>
/// </remarks>
public sealed class FileSystemCheckpointer : IWorkflowCheckpointer
{
    private sealed class LockEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ActiveUsers { get; set; }
    }

    private static readonly JsonSerializerOptions s_jsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // Per-session writer locks — prevents torn writes under concurrent access.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, LockEntry> _locks = new();
    private readonly object _locksGate = new();
    private readonly string _directory;

    /// <summary>
    /// Creates a new <see cref="FileSystemCheckpointer"/> that writes files to
    /// <paramref name="directory"/>. The directory is created if it does not exist.
    /// </summary>
    /// <param name="directory">
    /// Path to the directory where checkpoint files are stored.
    /// Use an absolute path to avoid ambiguity.
    /// </param>
    public FileSystemCheckpointer(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    /// <inheritdoc/>
    public async ValueTask SaveAsync(WorkflowSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var filePath = GetFilePath(snapshot.SessionId);
        var json = JsonSerializer.Serialize(snapshot, s_jsonOptions);

        var lockEntry = AcquireLockEntry(snapshot.SessionId);
        await lockEntry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.WriteAllTextAsync(filePath, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lockEntry.Semaphore.Release();
            ReleaseLockEntry(snapshot.SessionId, lockEntry);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<WorkflowSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var filePath = GetFilePath(sessionId);
        if (!File.Exists(filePath))
            return null;

        var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<WorkflowSnapshot>(json, s_jsonOptions);
    }

    /// <inheritdoc/>
    public ValueTask DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var filePath = GetFilePath(sessionId);
        if (File.Exists(filePath))
            File.Delete(filePath);

        lock (_locksGate)
        {
            if (_locks.TryGetValue(sessionId, out var lockEntry) && lockEntry.ActiveUsers == 0)
            {
                _locks.TryRemove(sessionId, out _);
                lockEntry.Semaphore.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private string GetFilePath(string sessionId)
    {
        // Sanitize the session id so it can safely be used as a filename.
        var safe = string.Concat(sessionId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(_directory, $"{safe}.checkpoint.json");
    }

    private LockEntry AcquireLockEntry(string sessionId)
    {
        lock (_locksGate)
        {
            var lockEntry = _locks.GetOrAdd(sessionId, _ => new LockEntry());
            lockEntry.ActiveUsers++;
            return lockEntry;
        }
    }

    private void ReleaseLockEntry(string sessionId, LockEntry lockEntry)
    {
        lock (_locksGate)
        {
            lockEntry.ActiveUsers--;
            if (lockEntry.ActiveUsers == 0 && _locks.TryGetValue(sessionId, out var current) && ReferenceEquals(current, lockEntry))
            {
                _locks.TryRemove(sessionId, out _);
                lockEntry.Semaphore.Dispose();
            }
        }
    }
}
