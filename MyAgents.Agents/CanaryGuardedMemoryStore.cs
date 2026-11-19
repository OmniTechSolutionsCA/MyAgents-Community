using System.Text;
using MyAgents.Core.Memory;
using MyAgents.Core.Security;

namespace MyAgents.Agents;

/// <summary>
/// Wraps an <see cref="IMemoryStore"/> and scans every <see cref="SetAsync"/> payload through
/// <see cref="EgressGuard"/> on the <see cref="EgressChannel.MemoryWrite"/> channel before persisting.
/// </summary>
internal sealed class CanaryGuardedMemoryStore : IMemoryStore, IMemoryStoreChangeNotifier
{
    private readonly IMemoryStore _inner;
    private readonly ICanaryVault _vault;
    private readonly IEgressScanner? _scanner;
    private readonly ICanaryViolationHandler _handler;
    private readonly CanaryDefenseOptions _options;

    public CanaryGuardedMemoryStore(
        IMemoryStore inner,
        ICanaryVault vault,
        IEgressScanner? scanner,
        ICanaryViolationHandler handler,
        CanaryDefenseOptions options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);

        _inner = inner;
        _vault = vault;
        _scanner = scanner;
        _handler = handler;
        _options = options;
    }

    public async ValueTask SetAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!_options.ScanMemoryWrites)
        {
            await _inner.SetAsync(entry, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_vault.IsQuarantined)
            throw new CanaryViolationException("Session is quarantined; memory writes are refused.");

        var payload = BuildPayload(entry);

        var scan = await EgressGuard
            .ScanAsync(
                _vault.Canaries,
                _scanner,
                EgressChannel.MemoryWrite,
                payload,
                _options.MaxScanBytes,
                _options.FailOpenOnScannerError,
                cancellationToken)
            .ConfigureAwait(false);

        if (scan.Severity != EgressSeverity.Informational)
        {
            await CanaryEgressMiddleware.RecordAuditAsync(
                _options.AuditSink,
                _vault.SessionId,
                EgressChannel.MemoryWrite,
                scan.Findings,
                toolName: null,
                cancellationToken).ConfigureAwait(false);

            var disposition = await _handler
                .HandleAsync(
                    new CanaryViolationContext(EgressChannel.MemoryWrite, scan, _vault.SessionId),
                    cancellationToken)
                .ConfigureAwait(false);

            if (disposition == CanaryDisposition.Abort)
            {
                throw new CanaryViolationException(
                    $"Memory write to '{entry.Namespace}/{entry.Key}' was refused by egress defense on the {scan.Severity} channel.");
            }
        }

        await _inner.SetAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<MemoryEntry?> GetAsync(string ns, string key, CancellationToken cancellationToken = default)
        => _inner.GetAsync(ns, key, cancellationToken);

    public ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(
        string ns,
        string? query = null,
        int maxResults = 10,
        CancellationToken cancellationToken = default)
        => _inner.SearchAsync(ns, query, maxResults, cancellationToken);

    public ValueTask DeleteAsync(string ns, string key, CancellationToken cancellationToken = default)
        => _inner.DeleteAsync(ns, key, cancellationToken);

    public ValueTask ClearAsync(string ns, CancellationToken cancellationToken = default)
        => _inner.ClearAsync(ns, cancellationToken);

    public IDisposable Subscribe(Action<MemoryStoreChangedEvent> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return _inner is IMemoryStoreChangeNotifier notifier
            ? notifier.Subscribe(listener)
            : NoOpSubscription.Instance;
    }

    private static string BuildPayload(MemoryEntry entry)
    {
        var builder = new StringBuilder();
        builder.AppendLine(entry.Namespace);
        builder.AppendLine(entry.Key);
        builder.AppendLine(entry.Content);

        if (entry.Metadata is { Count: > 0 } metadata)
        {
            foreach (var pair in metadata)
            {
                builder.AppendLine(pair.Key);
                builder.AppendLine(pair.Value);
            }
        }

        return builder.ToString();
    }

    private sealed class NoOpSubscription : IDisposable
    {
        public static readonly NoOpSubscription Instance = new();

        public void Dispose()
        {
        }
    }
}
