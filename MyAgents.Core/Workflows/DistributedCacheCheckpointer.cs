using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace MyAgents.Core.Workflows;

/// <summary>
/// An <see cref="IWorkflowCheckpointer"/> backed by any <see cref="IDistributedCache"/>
/// implementation (e.g. Redis, SQL Server, Azure Blob via Garnet, or in-memory for tests).
/// </summary>
/// <remarks>
/// <para>
/// This checkpointer serialises each <see cref="WorkflowSnapshot"/> as UTF-8 JSON
/// and stores it under the key <c>myagents:checkpoint:{sessionId}</c>.
/// Key naming can be customised via the <paramref name="keyPrefix"/> constructor parameter.
/// </para>
/// <para>
/// To use with Redis:
/// <code>
/// // 1. Reference  Microsoft.Extensions.Caching.StackExchangeRedis
/// services.AddStackExchangeRedisCache(o => o.Configuration = "localhost:6379");
///
/// // 2. Register the checkpointer (IDistributedCache is resolved from DI)
/// services.AddSingleton&lt;IWorkflowCheckpointer, DistributedCacheCheckpointer&gt;();
/// </code>
/// </para>
/// <para>
/// To scope checkpoints to a particular expiry window use the
/// <paramref name="cacheOptions"/> overload:
/// <code>
/// var options = new DistributedCacheEntryOptions
/// {
///     AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(7)
/// };
/// new DistributedCacheCheckpointer(cache, options);
/// </code>
/// </para>
/// </remarks>
public sealed class DistributedCacheCheckpointer : IWorkflowCheckpointer
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IDistributedCache _cache;
    private readonly DistributedCacheEntryOptions _cacheOptions;
    private readonly string _keyPrefix;

    /// <param name="cache">
    /// The distributed cache backend (Redis, SQL Server, etc.). Must be registered in DI
    /// or passed directly.
    /// </param>
    /// <param name="cacheOptions">
    /// Optional sliding / absolute expiry settings. When <see langword="null"/> entries
    /// persist until explicitly removed or the cache evicts them.
    /// </param>
    /// <param name="keyPrefix">
    /// Prefix added to every cache key, e.g. <c>"myagents:checkpoint:"</c> (default).
    /// Change this to isolate checkpoints from other cache entries.
    /// </param>
    public DistributedCacheCheckpointer(
        IDistributedCache cache,
        DistributedCacheEntryOptions? cacheOptions = null,
        string keyPrefix = "myagents:checkpoint:")
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(keyPrefix);

        _cache = cache;
        _cacheOptions = cacheOptions ?? new DistributedCacheEntryOptions();
        _keyPrefix = keyPrefix;
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(WorkflowSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var key = BuildKey(snapshot.SessionId);
        var json = JsonSerializer.Serialize(snapshot, s_jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _cache.SetAsync(key, bytes, _cacheOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var key = BuildKey(sessionId);
        var bytes = await _cache.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
            return null;

        var json = Encoding.UTF8.GetString(bytes);
        return JsonSerializer.Deserialize<WorkflowSnapshot>(json, s_jsonOptions);
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var key = BuildKey(sessionId);
        await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
    }

    private string BuildKey(string sessionId) => $"{_keyPrefix}{sessionId}";
}
