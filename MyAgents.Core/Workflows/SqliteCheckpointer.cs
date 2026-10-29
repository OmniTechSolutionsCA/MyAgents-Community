using Microsoft.Data.Sqlite;

namespace MyAgents.Core.Workflows;

/// <summary>
/// SQLite-backed implementation of <see cref="IWorkflowCheckpointer"/>.
/// Stores workflow snapshots in a local SQLite database for durable persistence.
/// Configures write-ahead logging (WAL) for high throughput.
/// </summary>
/// <remarks>
/// Schema: Creates a "workflow_snapshots" table with columns:
/// - session_id (TEXT PRIMARY KEY)
/// - workflow_id (TEXT)
/// - state_version (INTEGER)
/// - state_json (TEXT)
/// - created_at (TEXT - ISO8601)
/// </remarks>
public sealed class SqliteCheckpointer : IWorkflowCheckpointer, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _tableName;
    private bool _disposed;

    private readonly SqliteCommand _saveCommand;
    private readonly SqliteParameter _saveSessionId;
    private readonly SqliteParameter _saveWorkflowId;
    private readonly SqliteParameter _saveStateVersion;
    private readonly SqliteParameter _saveStateJson;
    private readonly SqliteParameter _saveCreatedAt;

    private readonly SqliteCommand _loadCommand;
    private readonly SqliteParameter _loadSessionId;

    private readonly SqliteCommand _deleteCommand;
    private readonly SqliteParameter _deleteSessionId;

    /// <param name="databasePath">Path to the SQLite database file.</param>
    /// <param name="tableName">Table name for snapshots (default: "workflow_snapshots").</param>
    public SqliteCheckpointer(string databasePath, string tableName = "workflow_snapshots")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        _tableName = tableName;

        InitializeTable();

        _saveCommand = _connection.CreateCommand();
        _saveCommand.CommandText = $"""
            INSERT OR REPLACE INTO {_tableName} (session_id, workflow_id, state_version, state_json, created_at)
            VALUES (@session_id, @workflow_id, @state_version, @state_json, @created_at)
            """;
        _saveSessionId = _saveCommand.Parameters.Add("@session_id", SqliteType.Text);
        _saveWorkflowId = _saveCommand.Parameters.Add("@workflow_id", SqliteType.Text);
        _saveStateVersion = _saveCommand.Parameters.Add("@state_version", SqliteType.Integer);
        _saveStateJson = _saveCommand.Parameters.Add("@state_json", SqliteType.Text);
        _saveCreatedAt = _saveCommand.Parameters.Add("@created_at", SqliteType.Text);
        _saveCommand.Prepare();

        _loadCommand = _connection.CreateCommand();
        _loadCommand.CommandText = $"""
            SELECT session_id, workflow_id, state_version, state_json, created_at
            FROM {_tableName}
            WHERE session_id = @session_id
            """;
        _loadSessionId = _loadCommand.Parameters.Add("@session_id", SqliteType.Text);
        _loadCommand.Prepare();

        _deleteCommand = _connection.CreateCommand();
        _deleteCommand.CommandText = $"DELETE FROM {_tableName} WHERE session_id = @session_id";
        _deleteSessionId = _deleteCommand.Parameters.Add("@session_id", SqliteType.Text);
        _deleteCommand.Prepare();
    }

    private void InitializeTable()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS {_tableName} (
                session_id TEXT PRIMARY KEY,
                workflow_id TEXT NOT NULL,
                state_version INTEGER NOT NULL,
                state_json TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public async ValueTask SaveAsync(WorkflowSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(snapshot);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _saveSessionId.Value = snapshot.SessionId;
            _saveWorkflowId.Value = snapshot.WorkflowId;
            _saveStateVersion.Value = snapshot.StateVersion;
            _saveStateJson.Value = snapshot.StateJson;
            _saveCreatedAt.Value = DateTimeOffset.UtcNow.ToString("O");

            await _saveCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<WorkflowSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _loadSessionId.Value = sessionId;

            using var reader = await _loadCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new WorkflowSnapshot(
                    WorkflowId: reader.GetString(1),
                    StateVersion: reader.GetInt32(2),
                    SessionId: reader.GetString(0),
                    StateJson: reader.GetString(3));
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _deleteSessionId.Value = sessionId;

            await _deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveCommand.Dispose();
        _loadCommand.Dispose();
        _deleteCommand.Dispose();
        _gate.Dispose();
        _connection.Close();
        _connection.Dispose();
    }
}
