using System.Threading.Channels;

namespace MyAgents.Core.Messaging;

// ── Message envelope ──────────────────────────────────────────────────────────

/// <summary>
/// An <see cref="AgentMessage"/> routed through a channel, enriched with routing metadata.
/// </summary>
/// <param name="Message">The payload message.</param>
/// <param name="SenderAgentName">Name of the agent that published this envelope.</param>
/// <param name="TargetAgentName">
/// Intended recipient agent name, or <c>null</c> for broadcast-to-all.
/// </param>
/// <param name="PublishedAt">UTC timestamp when the envelope was created.</param>
public sealed record ChannelEnvelope(
    AgentMessage Message,
    string SenderAgentName,
    string? TargetAgentName = null,
    DateTimeOffset? PublishedAt = null);

// ── Channel contract ─────────────────────────────────────────────────────────

/// <summary>
/// An in-process pub/sub channel that lets agents communicate without being
/// pre-wired to each other in a workflow topology.
///
/// <list type="bullet">
///   <item><description>Any agent can <b>publish</b> a message to the channel.</description></item>
///   <item><description>Any subscribed reader receives <b>all</b> messages (broadcast) unless filtered by <c>TargetAgentName</c>.</description></item>
///   <item><description>The channel is one-to-many: a single publish fans out to all active readers.</description></item>
/// </list>
/// </summary>
public interface IAgentChannel
{
    /// <summary>
    /// Publishes an envelope onto the channel. The call returns immediately after
    /// the envelope is queued; delivery is asynchronous.
    /// </summary>
    /// <param name="envelope">The envelope to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask PublishAsync(ChannelEnvelope envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a new subscription reader. Each call returns an independent reader
    /// that receives all envelopes published after this call.
    /// The caller must dispose the reader when done.
    /// </summary>
    IChannelSubscription Subscribe();
}

/// <summary>
/// A subscription reader returned by <see cref="IAgentChannel.Subscribe"/>.
/// Dispose to unsubscribe.
/// </summary>
public interface IChannelSubscription : IAsyncDisposable
{
    /// <summary>
    /// Reads the next available envelope, blocking asynchronously until one arrives
    /// or the subscription is closed.
    /// </summary>
    IAsyncEnumerable<ChannelEnvelope> ReadAllAsync(CancellationToken cancellationToken = default);
}

// ── In-process implementation ─────────────────────────────────────────────────

/// <summary>
/// In-process <see cref="IAgentChannel"/> backed by <see cref="System.Threading.Channels"/>.
/// Suitable for single-process, multi-agent coordination.
/// </summary>
/// <remarks>
/// The fan-out model: every <see cref="Subscribe"/> call gets its own bounded
/// <see cref="Channel{T}"/>. Publishing writes to ALL subscriber channels concurrently.
/// Replace with a Service Bus or SignalR implementation for distributed scenarios.
/// </remarks>
public sealed class InMemoryAgentChannel : IAgentChannel, IDisposable
{
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly List<Channel<ChannelEnvelope>> _subscribers = new();
    private long _droppedMessages;
    private bool _disposed;

    /// <summary>
    /// Raised when a message is dropped because a subscriber's buffer is full.
    /// The argument is the dropped envelope.
    /// Keep handlers fast; they are called synchronously on the publish thread.
    /// </summary>
    public event Action<ChannelEnvelope>? MessageDropped;

    /// <summary>
    /// Total number of messages dropped across all subscribers since this channel was created.
    /// Incremented atomically; safe to read from any thread.
    /// </summary>
    public long DroppedMessages => Interlocked.Read(ref _droppedMessages);

    /// <param name="capacity">
    /// Per-subscriber channel capacity. Defaults to 256.
    /// When a subscriber's buffer is full the incoming message is dropped and
    /// <see cref="MessageDropped"/> is raised / <see cref="DroppedMessages"/> is incremented.
    /// </param>
    public InMemoryAgentChannel(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <inheritdoc/>
    public ValueTask PublishAsync(
        ChannelEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var envelope2 = envelope with { PublishedAt = envelope.PublishedAt ?? DateTimeOffset.UtcNow };

        List<Channel<ChannelEnvelope>> snapshot;
        lock (_lock) snapshot = new List<Channel<ChannelEnvelope>>(_subscribers);

        foreach (var ch in snapshot)
        {
            // TryWrite returns false when the channel is full (DropWrite mode).
            // This makes drops explicit and observable rather than silently discarding
            // an older message inside the channel implementation.
            if (!ch.Writer.TryWrite(envelope2))
            {
                Interlocked.Increment(ref _droppedMessages);
                MessageDropped?.Invoke(envelope2);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public IChannelSubscription Subscribe()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var channel = Channel.CreateBounded<ChannelEnvelope>(
            new BoundedChannelOptions(_capacity)
            {
                FullMode         = BoundedChannelFullMode.DropWrite,
                SingleReader     = true,
                SingleWriter     = false,
                AllowSynchronousContinuations = false,
            });

        lock (_lock) _subscribers.Add(channel);

        return new Subscription(channel, this);
    }

    internal void Unsubscribe(Channel<ChannelEnvelope> channel)
    {
        lock (_lock) _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        List<Channel<ChannelEnvelope>> snapshot;
        lock (_lock)
        {
            snapshot = new List<Channel<ChannelEnvelope>>(_subscribers);
            _subscribers.Clear();
        }

        foreach (var ch in snapshot)
            ch.Writer.TryComplete();
    }

    // ── Inner subscription reader ─────────────────────────────────────────────

    private sealed class Subscription : IChannelSubscription
    {
        private readonly Channel<ChannelEnvelope> _channel;
        private readonly InMemoryAgentChannel _parent;
        private bool _disposed;

        public Subscription(Channel<ChannelEnvelope> channel, InMemoryAgentChannel parent)
        {
            _channel = channel;
            _parent  = parent;
        }

        public IAsyncEnumerable<ChannelEnvelope> ReadAllAsync(
            CancellationToken cancellationToken = default)
            => _channel.Reader.ReadAllAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                _parent.Unsubscribe(_channel);
            }
            return ValueTask.CompletedTask;
        }
    }
}
