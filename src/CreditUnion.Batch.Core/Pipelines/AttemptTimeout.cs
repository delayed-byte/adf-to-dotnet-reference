namespace CreditUnion.Batch.Core.Pipelines;

/// <summary>
/// The ADF activity <c>timeout</c>, enforced for one attempt (ADR 0009). It is a timeout, not a retry:
/// it surfaces as a <see cref="TimeoutException"/>, which the orchestrator's <c>RetryPolicy</c> may retry.
/// </summary>
public static class AttemptTimeout
{
    public static async Task<T> RunAsync<T>(string activity, TimeSpan timeout, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(timeout);
        try
        {
            return await work(attempt.Token);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{activity} exceeded its ADF timeout of {timeout}.");
        }
    }
}
