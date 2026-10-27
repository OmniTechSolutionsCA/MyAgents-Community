namespace MyAgents.Core;

/// <summary>
/// Default error classifier that considers common transient exceptions (HttpRequestException, TimeoutException, IOException) as retryable.
/// </summary>
public sealed class DefaultTransientErrorClassifier : ITransientErrorClassifier
{
    public bool IsTransient(Exception exception)
    {
        return exception switch
        {
            HttpRequestException => true,
            TimeoutException => true,
            IOException => true,
            _ => false
        };
    }
}
