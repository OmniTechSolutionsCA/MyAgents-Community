namespace MyAgents.Core;

/// <summary>
/// Classifies exceptions as transient (retryable) or terminal.
/// </summary>
public interface ITransientErrorClassifier
{
    /// <summary>
    /// Determines whether an exception is transient and should trigger a retry.
    /// </summary>
    /// <param name="exception">The exception that occurred.</param>
    /// <returns>True if the error is transient; false otherwise.</returns>
    bool IsTransient(Exception exception);
}
