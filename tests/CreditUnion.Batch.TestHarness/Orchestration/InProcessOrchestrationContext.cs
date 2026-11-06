using System.Security.Cryptography;
using System.Text;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace CreditUnion.Batch.TestHarness.Orchestration;

/// <summary>
/// A <see cref="TaskOrchestrationContext"/> that executes activities and sub-orchestrations in process
/// (ADR 0008). It keeps the Durable rules a conversion can get wrong:
/// <list type="bullet">
/// <item>inputs and outputs cross every boundary as JSON, through the same <see cref="Microsoft.DurableTask.Converters.JsonDataConverter"/>;</item>
/// <item>failures cross as <see cref="TaskFailedException"/> carrying only type name, message and stack (no exception objects);</item>
/// <item><see cref="RetryPolicy"/> and retry handlers are applied as the Durable Task framework applies them, with durable timers on the virtual clock;</item>
/// <item>scheduled work starts only when the orchestrator yields, and orchestrator code must stay on its own thread.</item>
/// </list>
/// It does not replay orchestrators, so it cannot detect non-deterministic orchestrator code.
/// </summary>
internal sealed class InProcessOrchestrationContext : TaskOrchestrationContext
{
    private readonly InProcessFunctionHost host;
    private readonly OrchestrationPump pump;
    private readonly string? serializedInput;
    private int nextTaskId;
    private int guidCounter;

    public InProcessOrchestrationContext(InProcessFunctionHost host, OrchestrationPump pump, TaskName name, string instanceId, ParentOrchestrationInstance? parent, string? serializedInput)
    {
        this.host = host;
        this.pump = pump;
        this.serializedInput = serializedInput;
        Name = name;
        InstanceId = instanceId;
        Parent = parent;
    }

    public override TaskName Name { get; }

    public override string InstanceId { get; }

    public override ParentOrchestrationInstance? Parent { get; }

    public override DateTime CurrentUtcDateTime => host.Clock.GetUtcNow().UtcDateTime;

    public override bool IsReplaying => false;

    public object? CustomStatus { get; private set; }

    protected override ILoggerFactory LoggerFactory => host.LoggerFactory;

    public override T GetInput<T>() where T : default =>
        serializedInput is null ? default! : host.Converter.Deserialize<T>(serializedInput)!;

    public override Task<TResult> CallActivityAsync<TResult>(TaskName name, object? input = null, TaskOptions? options = null) =>
        CallAsync<TResult>(DurableTaskKind.Activity, name, input, options);

    public override Task<TResult> CallSubOrchestratorAsync<TResult>(TaskName orchestratorName, object? input = null, TaskOptions? options = null) =>
        CallAsync<TResult>(DurableTaskKind.SubOrchestration, orchestratorName, input, options);

    public override Task CreateTimer(DateTime fireAt, CancellationToken cancellationToken)
    {
        EnsureOrchestratorThread();
        var utc = fireAt.Kind switch
        {
            DateTimeKind.Local => fireAt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(fireAt, DateTimeKind.Utc),
        };
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.WhenIdle(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                fired.TrySetCanceled(cancellationToken);
                return;
            }

            host.Clock.AdvanceTo(new DateTimeOffset(utc));
            fired.TrySetResult();
        });
        return fired.Task;
    }

    public override Task<T> WaitForExternalEvent<T>(string eventName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("External events are not used by the batch and are not supported by the in-process runner.");

    public override void SendEvent(string instanceId, string eventName, object payload) =>
        throw new NotSupportedException("Events are not used by the batch and are not supported by the in-process runner.");

    public override void SetCustomStatus(object? customStatus) => CustomStatus = customStatus;

    public override void ContinueAsNew(object? newInput = null, bool preserveUnprocessedEvents = true) =>
        throw new NotSupportedException("ContinueAsNew is not used by the batch and is not supported by the in-process runner.");

    /// <summary>Deterministic per instance, like Durable's name-based GUIDs.</summary>
    public override Guid NewGuid()
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{InstanceId}:{guidCounter++}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private async Task<TResult> CallAsync<TResult>(DurableTaskKind kind, TaskName name, object? input, TaskOptions? options)
    {
        EnsureOrchestratorThread();
        var callId = host.Trace.NewCallId();
        var serialized = host.Converter.Serialize(input);
        var childInstanceId = kind == DurableTaskKind.SubOrchestration
            ? (options as SubOrchestrationOptions)?.InstanceId ?? NewGuid().ToString("N")
            : null;
        var policy = options?.Retry?.Policy;
        var handler = options?.Retry?.Handler;
        var firstAttemptAt = CurrentUtcDateTime;
        for (var attempt = 1; ; attempt++)
        {
            var taskId = nextTaskId++;
            var (output, failure) = await ScheduleAsync(kind, name, serialized, callId, attempt, policy, childInstanceId);
            if (failure is null)
            {
                return output is null ? default! : host.Converter.Deserialize<TResult>(output)!;
            }

            var exception = new TaskFailedException(name.Name, taskId, failure);
            var delay = await NextRetryDelayAsync(kind, policy, handler, attempt, exception, firstAttemptAt);
            host.Trace.Add(new RetryDecision(callId, attempt, delay));
            if (delay is null)
            {
                throw exception;
            }

            if (delay > TimeSpan.Zero)
            {
                await CreateTimer(CurrentUtcDateTime + delay.Value, CancellationToken.None);
            }
        }
    }

    // DurableTask.Core RetryInterceptor: attempts up to MaxNumberOfAttempts; a failure is retried when
    // Handle accepts it and the retry timeout (measured from the first attempt) has not passed; the
    // delay is FirstRetryInterval * BackoffCoefficient^(retries so far), capped at MaxRetryInterval.
    // Handle receives the Core exception the interceptor sees (RetryPolicy.HandleFailure relies on it).
    private async Task<TimeSpan?> NextRetryDelayAsync(DurableTaskKind kind, RetryPolicy? policy, AsyncRetryHandler? handler, int attempt, TaskFailedException failure, DateTime firstAttemptAt)
    {
        if (handler is not null)
        {
            var retry = await handler(new RetryContext(this, attempt, failure.FailureDetails, CurrentUtcDateTime - firstAttemptAt, CancellationToken.None));
            return retry ? TimeSpan.Zero : null;
        }

        if (policy is null || attempt >= policy.MaxNumberOfAttempts)
        {
            return null;
        }

        if (!policy.Handle(ToCoreException(kind, failure)))
        {
            return null;
        }

        if (policy.RetryTimeout != TimeSpan.MaxValue && policy.RetryTimeout != Timeout.InfiniteTimeSpan && CurrentUtcDateTime >= firstAttemptAt + policy.RetryTimeout)
        {
            return null;
        }

        var delay = TimeSpan.FromMilliseconds(policy.FirstRetryInterval.TotalMilliseconds * Math.Pow(policy.BackoffCoefficient, attempt - 1));
        return delay < policy.MaxRetryInterval ? delay : policy.MaxRetryInterval;
    }

    private static Exception ToCoreException(DurableTaskKind kind, TaskFailedException failure)
    {
        static DurableTask.Core.FailureDetails Core(TaskFailureDetails d) =>
            new(d.ErrorType, d.ErrorMessage, d.StackTrace, d.InnerFailure is null ? null : Core(d.InnerFailure), isNonRetriable: false);

        return kind == DurableTaskKind.Activity
            ? new DurableTask.Core.Exceptions.TaskFailedException(0, failure.TaskId, failure.TaskName, string.Empty, failure.FailureDetails.ErrorMessage, null!)
            {
                FailureDetails = Core(failure.FailureDetails),
            }
            : new DurableTask.Core.Exceptions.SubOrchestrationFailedException(0, failure.TaskId, failure.TaskName, string.Empty, failure.FailureDetails.ErrorMessage, null!)
            {
                FailureDetails = Core(failure.FailureDetails),
            };
    }

    private Task<(string? Output, TaskFailureDetails? Failure)> ScheduleAsync(
        DurableTaskKind kind, TaskName name, string? serialized, int callId, int attempt, RetryPolicy? policy, string? childInstanceId)
    {
        var scheduled = host.Trace.NextSequence();
        var scheduledAt = host.Clock.GetUtcNow();
        var completion = new TaskCompletionSource<(string?, TaskFailureDetails?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.WhenIdle(() => _ = Task.Run(async () =>
        {
            var started = host.Trace.NextSequence();
            var (output, failure) = kind == DurableTaskKind.Activity
                ? await host.ExecuteActivityAsync(name.Name, serialized).ConfigureAwait(false)
                : await host.ExecuteSubOrchestrationAsync(name.Name, childInstanceId!, serialized, new ParentOrchestrationInstance(Name, InstanceId)).ConfigureAwait(false);
            var completed = host.Trace.NextSequence();
            host.Trace.Add(new TaskAttempt(callId, Name.Name, InstanceId, kind, name.Name, attempt, policy, scheduled, started, completed, scheduledAt, childInstanceId, failure));
            completion.SetResult((output, failure));
        }));
        return completion.Task;
    }

    private void EnsureOrchestratorThread()
    {
        if (!pump.IsOrchestratorThread)
        {
            throw new InvalidOperationException(
                "Multithreaded execution was detected. This can happen if the orchestrator function code awaits a task that was not created by a " +
                "TaskOrchestrationContext method, or uses ConfigureAwait(false). See https://learn.microsoft.com/azure/azure-functions/durable/durable-functions-code-constraints.");
        }
    }
}
