using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MyAgents.Core;

namespace MyAgents.Runtime.Workers.Sqlite;

/// <summary>
/// SQLite-backed <see cref="ISessionApprovalMemory"/> that persists session-level
/// tool approval grants across worker restarts and suspend/resume boundaries.
/// </summary>
public sealed class SqliteSessionApprovalMemory : ISessionApprovalMemory, IDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection? _inMemoryKeepAlive;

    /// <summary>Creates a persistent session approval store at the given SQLite connection string.</summary>
    public SqliteSessionApprovalMemory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;

        if (connectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            _inMemoryKeepAlive = new SqliteConnection(connectionString);
            _inMemoryKeepAlive.Open();
            EnsureSchema(_inMemoryKeepAlive);
        }
        else
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            EnsureSchema(connection);
        }
    }

    private async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (_inMemoryKeepAlive is not null)
            return _inMemoryKeepAlive;

        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS session_approval_grants (
                session_id TEXT NOT NULL,
                scope INTEGER NOT NULL,
                tool_name TEXT NOT NULL,
                arguments_hash TEXT NOT NULL,
                status INTEGER NOT NULL,
                decided_at TEXT NOT NULL,
                decided_by TEXT NULL,
                reason TEXT NULL,
                modified_arguments_json TEXT NULL,
                PRIMARY KEY (session_id, scope, tool_name, arguments_hash)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public async ValueTask RecordDecisionAsync(
        ToolApprovalRequest request,
        ToolApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decision);

        if (decision.Status != ToolApprovalStatus.Approved || decision.Scope == ApprovalScope.Once)
            return;

        var toolName = decision.Scope == ApprovalScope.SessionAlways ? string.Empty : request.ToolName;
        var argsHash = decision.Scope == ApprovalScope.SessionCall ? ComputeHash(request.ArgumentsJson) : string.Empty;

        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO session_approval_grants (
                session_id, scope, tool_name, arguments_hash,
                status, decided_at, decided_by, reason, modified_arguments_json
            )
            VALUES ($sessionId, $scope, $toolName, $argsHash, $status, $decidedAt, $decidedBy, $reason, $modifiedArgs)
            ON CONFLICT (session_id, scope, tool_name, arguments_hash) DO UPDATE SET
                status = excluded.status,
                decided_at = excluded.decided_at,
                decided_by = excluded.decided_by,
                reason = excluded.reason,
                modified_arguments_json = excluded.modified_arguments_json;
            """;
        cmd.Parameters.AddWithValue("$sessionId", request.SessionId);
        cmd.Parameters.AddWithValue("$scope", (int)decision.Scope);
        cmd.Parameters.AddWithValue("$toolName", toolName);
        cmd.Parameters.AddWithValue("$argsHash", argsHash);
        cmd.Parameters.AddWithValue("$status", (int)decision.Status);
        cmd.Parameters.AddWithValue("$decidedAt", decision.DecidedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$decidedBy", (object?)decision.DecidedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$reason", (object?)decision.Reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$modifiedArgs", (object?)decision.ModifiedArgumentsJson ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_inMemoryKeepAlive is null)
                await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ToolApprovalDecision?> TryGetGrantedDecisionAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var argsHash = ComputeHash(request.ArgumentsJson);

        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT scope, status, decided_at, decided_by, reason, modified_arguments_json
            FROM session_approval_grants
            WHERE session_id = $sessionId
              AND (
                    (scope = $scopeAll AND tool_name = '' AND arguments_hash = '')
                 OR (scope = $scopeTool AND lower(tool_name) = lower($toolName) AND arguments_hash = '')
                 OR (scope = $scopeCall AND lower(tool_name) = lower($toolName) AND arguments_hash = $argsHash)
              )
            ORDER BY scope DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$sessionId", request.SessionId);
        cmd.Parameters.AddWithValue("$scopeAll", (int)ApprovalScope.SessionAlways);
        cmd.Parameters.AddWithValue("$scopeTool", (int)ApprovalScope.SessionTool);
        cmd.Parameters.AddWithValue("$scopeCall", (int)ApprovalScope.SessionCall);
        cmd.Parameters.AddWithValue("$toolName", request.ToolName);
        cmd.Parameters.AddWithValue("$argsHash", argsHash);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var scope = (ApprovalScope)reader.GetInt32(0);
        var status = (ToolApprovalStatus)reader.GetInt32(1);
        var decidedAt = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
        var decidedBy = reader.IsDBNull(3) ? null : reader.GetString(3);
        var reason = reader.IsDBNull(4) ? null : reader.GetString(4);
        var modifiedArgs = reader.IsDBNull(5) ? null : reader.GetString(5);

        return new ToolApprovalDecision(status, decidedAt, decidedBy, reason, scope, modifiedArgs);
        }
        finally
        {
            if (_inMemoryKeepAlive is null)
                await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask ClearSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM session_approval_grants WHERE session_id = $sessionId;";
        cmd.Parameters.AddWithValue("$sessionId", sessionId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_inMemoryKeepAlive is null)
                await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string ComputeHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }

    /// <inheritdoc />
    public void Dispose() => _inMemoryKeepAlive?.Dispose();
}
