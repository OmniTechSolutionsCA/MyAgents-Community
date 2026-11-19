namespace MyAgents.Core.Security;

/// <summary>
/// Default <see cref="ICanaryVault"/>. One instance per session, created during assembly.
/// </summary>
internal sealed class SessionCanaryVault : ICanaryVault
{
    private readonly string[] _canaries;
    private int _quarantined;
    private string? _quarantineReason;

    public SessionCanaryVault(string sessionId, IEnumerable<string> canaries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(canaries);

        SessionId = sessionId;
        _canaries = canaries
            .Where(static canary => !string.IsNullOrWhiteSpace(canary))
            .ToArray();
    }

    public string SessionId { get; }

    public IReadOnlyList<string> Canaries => _canaries;

    public bool IsQuarantined => Volatile.Read(ref _quarantined) != 0;

    public string? QuarantineReason => Volatile.Read(ref _quarantineReason);

    public void Quarantine(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        // Monotonic: the first caller wins, and no later call can clear or overwrite it.
        if (Interlocked.Exchange(ref _quarantined, 1) == 0)
            Volatile.Write(ref _quarantineReason, reason);
    }
}
