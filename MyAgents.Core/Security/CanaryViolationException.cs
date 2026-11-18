namespace MyAgents.Core.Security;

/// <summary>
/// Raised when egress defense aborts a turn or a tool call.
/// </summary>
/// <remarks>
/// <para>
/// Implements <see cref="INonRetryableException"/>: the cause of the abort cannot change by
/// re-running the turn, so <see cref="ResilienceMiddleware"/> must never retry it — even under a
/// classifier that reports every exception as transient.
/// </para>
/// <para>
/// <see cref="Exception.Message"/> is deliberately a fixed, uniform string. Detail lives in
/// <see cref="Reason"/>, which is for host diagnostics, logs and audit sinks only. Never put
/// <see cref="Reason"/> back into the conversation or return it to a caller that will show it to
/// the model: it names the detector that fired, which is exactly the feedback an attacker needs to
/// iterate against the detectors.
/// </para>
/// </remarks>
public sealed class CanaryViolationException : Exception, INonRetryableException
{
    private const string UniformMessage = "The turn was aborted by egress defense.";

    /// <summary>Creates a violation with no recorded reason.</summary>
    public CanaryViolationException()
        : base(UniformMessage) => Reason = UniformMessage;

    /// <summary>
    /// Creates a violation.
    /// </summary>
    /// <param name="reason">Host-facing diagnostic detail. Not used as the exception message.</param>
    public CanaryViolationException(string reason)
        : base(UniformMessage) => Reason = reason ?? UniformMessage;

    /// <summary>
    /// Creates a violation wrapping an inner exception.
    /// </summary>
    /// <param name="reason">Host-facing diagnostic detail. Not used as the exception message.</param>
    /// <param name="innerException">The underlying failure.</param>
    public CanaryViolationException(string reason, Exception innerException)
        : base(UniformMessage, innerException) => Reason = reason ?? UniformMessage;

    /// <summary>
    /// Host-facing diagnostic detail: which detector fired and on what. Never show this to the model.
    /// </summary>
    public string Reason { get; }
}
