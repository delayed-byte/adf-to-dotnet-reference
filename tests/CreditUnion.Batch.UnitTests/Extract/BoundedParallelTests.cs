using CreditUnion.Batch.Core.Concurrency;

namespace CreditUnion.Batch.UnitTests.Extract;

public sealed class BoundedParallelTests
{
    [Fact]
    public async Task No_more_than_the_batch_count_run_at_once_and_a_new_one_starts_as_soon_as_one_finishes()
    {
        var gates = Enumerable.Range(0, 5).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var started = new List<int>();

        var all = BoundedParallel.ForEachAsync(Enumerable.Range(0, 5), 3, async i =>
        {
            lock (started)
            {
                started.Add(i);
            }

            await gates[i].Task;
        });

        Snapshot(started).ShouldBe([0, 1, 2], "three start at once");
        gates[1].SetResult();
        await WaitForAsync(() => Snapshot(started).Length == 4);
        Snapshot(started).ShouldBe([0, 1, 2, 3], "the fourth starts when one finishes");
        gates[0].SetResult();
        gates[2].SetResult();
        await WaitForAsync(() => Snapshot(started).Length == 5);
        Snapshot(started).ShouldBe([0, 1, 2, 3, 4]);
        all.IsCompleted.ShouldBeFalse();
        gates[3].SetResult();
        gates[4].SetResult();
        await all;
    }

    [Fact]
    public async Task A_failure_stops_new_iterations_waits_for_running_ones_and_rethrows_the_first_failure()
    {
        var started = new List<int>();
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishedSlow = false;

        var all = BoundedParallel.ForEachAsync(Enumerable.Range(0, 6), 2, async i =>
        {
            lock (started)
            {
                started.Add(i);
            }

            if (i == 0)
            {
                await slow.Task;
                finishedSlow = true;
                return;
            }

            await Task.Yield();
            throw new InvalidOperationException("boom " + i);
        });
        await Task.Delay(50, TestContext.Current.CancellationToken);

        all.IsCompleted.ShouldBeFalse("iteration 0 is still running");
        slow.SetResult();
        var error = await Should.ThrowAsync<InvalidOperationException>(() => all);

        error.Message.ShouldBe("boom 1");
        finishedSlow.ShouldBeTrue("running iterations are awaited");
        Snapshot(started).ShouldBe([0, 1], "nothing starts after the failure");
    }

    private static int[] Snapshot(List<int> list)
    {
        lock (list)
        {
            return [.. list];
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            (DateTime.UtcNow < deadline).ShouldBeTrue("condition not reached within 5 seconds");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task An_empty_list_completes_and_the_limit_must_be_positive()
    {
        await BoundedParallel.ForEachAsync(Array.Empty<int>(), 3, _ => Task.CompletedTask);
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => BoundedParallel.ForEachAsync([1], 0, _ => Task.CompletedTask));
    }
}
