namespace MyAgents.Core;

/// <summary>
/// Marks an exception as terminal: <see cref="ResilienceMiddleware"/> never retries it,
/// regardless of what the configured <see cref="ITransientErrorClassifier"/> reports.
/// </summary>
/// <remarks>
/// <para>
/// Use this for failures where a retry is not merely useless but actively wrong — a security
/// violation, a policy rejection, or any abort whose cause cannot change by re-running the turn.
/// </para>
/// <para>
/// <see cref="ITransientErrorClassifier"/> answers what <em>tends</em> to be transient from an
/// exception's type. That inference is deliberately coarse: an application may inject a classifier
/// that reports every exception as transient, which would silently replay an abort that must be
/// terminal. Implementing this interface lets the throwing site state what is <em>known</em> to be
/// terminal, and it takes precedence over any classifier.
/// </para>
/// <para>
/// An exception implementing this interface is rethrown without the retry-exhausted metric being
/// recorded, because no retry was attempted — it was never a retry-exhaustion.
/// </para>
/// </remarks>
public interface INonRetryableException
{
}
