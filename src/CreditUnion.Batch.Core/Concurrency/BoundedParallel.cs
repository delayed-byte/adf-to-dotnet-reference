using System.Runtime.ExceptionServices;

namespace CreditUnion.Batch.Core.Concurrency;

/// <summary>
/// ADF's ForEach with <c>batchCount</c>: at most N iterations in flight, a new one starting as soon as
/// one finishes. Built on <see cref="Task.WhenAny(IEnumerable{Task})"/> only, so it is safe inside a
/// Durable orchestrator (no threads, timers or clocks of its own); never use ConfigureAwait(false) around it.
/// </summary>
public static class BoundedParallel
{
    /// <summary>
    /// Runs <paramref name="body"/> for every item with at most <paramref name="maxConcurrency"/> running at once.
    /// Once an iteration has failed no further iteration is started; running ones are awaited, then the
    /// first failure is rethrown.
    /// </summary>
    public static async Task ForEachAsync<T>(IEnumerable<T> items, int maxConcurrency, Func<T, Task> body)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        var running = new List<Task>(maxConcurrency);
        Exception? failure = null;
        foreach (var item in items)
        {
            while (failure is null && running.Count >= maxConcurrency)
            {
                failure = await ReapOneAsync(running);
            }

            if (failure is not null)
            {
                break;
            }

            running.Add(body(item));
        }

        while (running.Count > 0)
        {
            failure ??= await ReapOneAsync(running);
            if (failure is not null && running.Count > 0)
            {
                // Observe every iteration still running so none is left unobserved; the first failure is the one reported.
                foreach (var task in running.ToArray())
                {
                    try
                    {
                        await task;
                    }
                    catch (Exception)
                    {
                        // Reported failure is the first one.
                    }
                }

                break;
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static async Task<Exception?> ReapOneAsync(List<Task> running)
    {
        var finished = await Task.WhenAny(running);
        running.Remove(finished);
        try
        {
            await finished;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
