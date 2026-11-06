using System.Collections.Concurrent;

namespace CreditUnion.Batch.TestHarness.Orchestration;

/// <summary>
/// Runs one orchestrator on a dedicated thread with a single-threaded synchronization context, as the
/// Durable Task framework does: every continuation of the orchestrator resumes on that thread, and
/// work the orchestrator schedules (activities, sub-orchestrations, timers) is dispatched only when it
/// has nothing left to run, i.e. at the end of what Durable calls an episode. Parallel fan-out with
/// <c>Task.WhenAll</c> therefore really runs in parallel, while awaiting each call in turn does not.
/// </summary>
internal sealed class OrchestrationPump : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = [];
    private readonly Queue<Action> whenIdle = new();
    private int threadId;
    private Exception? fault;

    public bool IsOrchestratorThread => Environment.CurrentManagedThreadId == threadId;

    public static Task<T> Run<T>(string name, Func<OrchestrationPump, Task<T>> body)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var pump = new OrchestrationPump { threadId = Environment.CurrentManagedThreadId };
            SetSynchronizationContext(pump);
            Task<T> task;
            try
            {
                task = body(pump);
            }
            catch (Exception ex)
            {
                task = Task.FromException<T>(ex);
            }

            task.ContinueWith(_ => pump.queue.CompleteAdding(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            pump.Loop();
            if (pump.fault is not null)
            {
                result.SetException(pump.fault);
            }
            else if (task.IsCompletedSuccessfully)
            {
                result.SetResult(task.Result);
            }
            else
            {
                result.SetException(task.Exception?.InnerException ?? new InvalidOperationException($"Orchestration {name} did not complete."));
            }
        })
        {
            IsBackground = true,
            Name = "orchestration " + name,
        };
        thread.Start();
        return result.Task;
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        try
        {
            queue.Add((d, state));
        }
        catch (InvalidOperationException)
        {
            // The orchestration has completed; a continuation of work it never awaited is dropped, as in Durable.
        }
    }

    public override void Send(SendOrPostCallback d, object? state) =>
        throw new NotSupportedException("Synchronous dispatch onto an orchestrator thread is not supported.");

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Queues work to dispatch once the orchestrator yields (the end of the current episode).</summary>
    public void WhenIdle(Action action)
    {
        if (!IsOrchestratorThread)
        {
            throw new InvalidOperationException("Work can be scheduled only from the orchestrator thread.");
        }

        whenIdle.Enqueue(action);
    }

    private void Loop()
    {
        while (true)
        {
            if (queue.TryTake(out var item))
            {
                Invoke(() => item.Callback(item.State));
                continue;
            }

            if (queue.IsAddingCompleted)
            {
                return;
            }

            while (whenIdle.TryDequeue(out var action))
            {
                Invoke(action);
            }

            if (!queue.TryTake(out item, Timeout.Infinite))
            {
                return;
            }

            Invoke(() => item.Callback(item.State));
        }
    }

    private void Invoke(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            fault ??= ex;
            queue.CompleteAdding();
        }
    }
}
