using System.Collections.Concurrent;
using CreditUnion.Batch.Core.Pipelines;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;

namespace CreditUnion.Batch.UnitTests.Harness;

// A tiny Functions "app" used to self-test the in-process Durable host (ADR 0008).

public sealed record SamplePayload(DateOnly Date, decimal Amount, string Text);

/// <summary>Makes the activity "Flaky" fail a set number of times per key.</summary>
public sealed class FlakyDependency
{
    private readonly ConcurrentDictionary<string, int> calls = new(StringComparer.Ordinal);

    public int FailuresBeforeSuccess { get; set; }

    public bool ShouldFail(string key) => calls.AddOrUpdate(key, 1, (_, n) => n + 1) <= FailuresBeforeSuccess;
}

public sealed class SampleActivities(FlakyDependency flaky, TimeProvider time)
{
    [Function("Echo")]
    public static SamplePayload Echo([ActivityTrigger] SamplePayload input) => input with { Text = input.Text + "!" };

    [Function("Flaky")]
    public string Flaky([ActivityTrigger] string key) =>
        flaky.ShouldFail(key) ? throw new TimeoutException("transient failure for " + key) : key;

    [Function("Reject")]
    public static Task Reject([ActivityTrigger] string value) => throw new InvalidDataException("invalid value " + value);

    [Function("Slow")]
    public static async Task<string> SlowAsync([ActivityTrigger] string key, CancellationToken cancellationToken)
    {
        await Task.Delay(100, cancellationToken);
        return key;
    }

    [Function("Now")]
    public DateTimeOffset Now([ActivityTrigger] string? unused, FunctionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return time.GetUtcNow();
    }
}

public static class SampleOrchestrators
{
    private static TaskOptions ThreeRetriesEvery30Seconds => TaskOptions.FromRetryPolicy(new RetryPolicy(4, TimeSpan.FromSeconds(30)));

    [Function("EchoTwice")]
    public static async Task<SamplePayload> EchoTwiceAsync([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var first = await context.CallActivityAsync<SamplePayload>("Echo", context.GetInput<SamplePayload>());
        return await context.CallActivityAsync<SamplePayload>("Echo", first);
    }

    [Function("RetryFlaky")]
    public static Task<string> RetryFlakyAsync([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.CallActivityAsync<string>("Flaky", context.GetInput<string>(), ThreeRetriesEvery30Seconds);
    }

    [Function("RetryOnlyTransient")]
    public static Task RetryOnlyTransientAsync([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var policy = new RetryPolicy(4, TimeSpan.FromSeconds(30)) { HandleFailure = f => f.IsCausedBy<TimeoutException>() };
        return context.CallActivityAsync("Reject", "x", TaskOptions.FromRetryPolicy(policy));
    }

    [Function("FanOut")]
    public static async Task FanOutAsync([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        await Task.WhenAll(context.CallSubOrchestratorAsync<string>("Child", "a"), context.CallSubOrchestratorAsync<string>("Child", "b"));
        await context.CallSubOrchestratorAsync<string>("Child", "c");
    }

    [Function("OneAtATime")]
    public static async Task OneAtATimeAsync([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        await context.CallSubOrchestratorAsync<string>("Child", "a");
        await context.CallSubOrchestratorAsync<string>("Child", "b");
    }

    [Function("Child")]
    public static Task<string> ChildAsync([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.CallActivityAsync<string>("Slow", context.GetInput<string>());
    }

    [Function("FailWithCode")]
    public static Task FailWithCodeAsync([OrchestrationTrigger] TaskOrchestrationContext context) =>
        throw new PipelineFailedException(PipelineErrorCodes.TxnCodeUnmapped, "Unmapped transaction codes: P2PD");

    [Function("WrapChildFailure")]
    public static async Task WrapChildFailureAsync([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await context.CallSubOrchestratorAsync("FailWithCode");
        }
        catch (TaskFailedException ex)
        {
            PipelineFailedException.TryParse(ex.FailureDetails.ErrorType, ex.FailureDetails.ErrorMessage, out var code, out var detail);
            throw new PipelineFailedException(PipelineErrorCodes.BatchStepFailed, $"FailWithCode failed ({code}): {detail}");
        }
    }

    [Function("LeavesItsThread")]
    public static async Task LeavesItsThreadAsync([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
#pragma warning disable CA2007 // The point of this sample: ConfigureAwait(false) moves an orchestrator off its thread.
        await context.CallActivityAsync<string>("Slow", "x").ConfigureAwait(false);
#pragma warning restore CA2007
        await context.CallActivityAsync<string>("Slow", "y");
    }

    [Function("WaitFiveMinutes")]
    public static async Task<string> WaitFiveMinutesAsync([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var start = context.CurrentUtcDateTime;
        await context.CreateTimer(start.AddMinutes(5), CancellationToken.None);
        var now = await context.CallActivityAsync<DateTimeOffset>("Now", "now");
        return $"{context.CurrentUtcDateTime - start}|{now.UtcDateTime - start}";
    }
}

public static class SampleTimer
{
    [Function("SampleTimer")]
    public static Task<string> RunAsync([TimerTrigger("0 0 3 * * *")] TimerInfo timer, [DurableClient] DurableTaskClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.ScheduleNewOrchestrationInstanceAsync("EchoTwice", new SamplePayload(new DateOnly(2026, 11, 4), 1.10m, "timer"));
    }
}
