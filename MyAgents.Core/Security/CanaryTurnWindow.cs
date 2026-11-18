using System.Text;

namespace MyAgents.Core.Security;

/// <summary>
/// A bounded, per-session ring of recent turn output, used to catch a canary that is split across
/// turns.
/// </summary>
/// <remarks>
/// <para>
/// Scanning each turn in isolation misses the cheapest evasion there is: emit one fragment per turn
/// and the canary never appears whole in any single payload. Concatenating recent turns closes that
/// without keeping the transcript forever.
/// </para>
/// <para>
/// Both bounds are enforced — turn count and retained bytes — and the oldest turn is evicted first.
/// An unbounded window would turn a long conversation into a growing scan cost and would keep
/// matching fragments the session moved past long ago, so eviction is a correctness property here,
/// not just an optimisation.
/// </para>
/// <para>
/// A turn longer than the byte budget is truncated to its <b>tail</b>: the end of a message is where a
/// fragment is most likely to sit, and truncating to the tail keeps the retained bytes within the
/// budget. Accounting is UTF-8 bytes, matching the scan cap, and truncation walks back by byte cost
/// rather than by character count -- a character can cost up to four bytes, so a character-count cap
/// is not a byte bound.
/// </para>
/// </remarks>
internal sealed class CanaryTurnWindow
{
    /// <summary>How many turns are retained by default.</summary>
    public const int DefaultTurns = 8;

    /// <summary>How many UTF-8 bytes of turn text are retained by default.</summary>
    public const int DefaultBytes = 8 * 1024;

    private readonly object _gate = new();
    private readonly Queue<string> _turns = new();
    private readonly int _maxTurns;
    private readonly int _maxBytes;
    private int _bytes;

    public CanaryTurnWindow(int maxTurns = DefaultTurns, int maxBytes = DefaultBytes)
    {
        if (maxTurns < 1)
            throw new ArgumentOutOfRangeException(nameof(maxTurns), "Must be at least 1.");

        if (maxBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "Must be at least 1.");

        _maxTurns = maxTurns;
        _maxBytes = maxBytes;
    }

    /// <summary>Number of turns currently retained. Exposed for tests.</summary>
    public int TurnCount
    {
        get
        {
            lock (_gate)
                return _turns.Count;
        }
    }

    /// <summary>UTF-8 bytes currently retained, never above the configured budget. Exposed for tests.</summary>
    public int RetainedBytes
    {
        get
        {
            lock (_gate)
                return _bytes;
        }
    }

    /// <summary>Returns the retained turns, oldest first.</summary>
    public string Snapshot()
    {
        lock (_gate)
            return string.Concat(_turns);
    }

    /// <summary>
    /// Adds a turn's text, evicting the oldest turns until both bounds hold again.
    /// </summary>
    public void Append(string turnText)
    {
        if (string.IsNullOrEmpty(turnText))
            return;

        var retained = TruncateToTail(turnText, _maxBytes);

        lock (_gate)
        {
            _turns.Enqueue(retained);
            _bytes += Encoding.UTF8.GetByteCount(retained);

            // Keep at least the newest turn: this loop cannot empty the window, and because each
            // entry is already within the byte budget, stopping at one turn always satisfies it.
            while (_turns.Count > _maxTurns || (_bytes > _maxBytes && _turns.Count > 1))
                _bytes -= Encoding.UTF8.GetByteCount(_turns.Dequeue());
        }
    }

    private static string TruncateToTail(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
            return value;

        // Walk back from the end counting UTF-8 bytes. Capping by character count is NOT a byte
        // bound: a character can cost up to four bytes, so a string shorter than the budget in
        // characters can still be over it in bytes. Surrogate pairs are kept whole.
        var start = value.Length;
        var bytes = 0;
        var index = value.Length;

        while (index > 0)
        {
            var width = index >= 2 && char.IsLowSurrogate(value[index - 1]) && char.IsHighSurrogate(value[index - 2])
                ? 2
                : 1;

            var cost = Encoding.UTF8.GetByteCount(value.AsSpan(index - width, width));
            if (bytes + cost > maxBytes)
                break;

            bytes += cost;
            index -= width;
            start = index;
        }

        return value[start..];
    }
}
