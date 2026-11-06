using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Converters;

namespace CreditUnion.Batch.TestHarness.Orchestration;

/// <summary>An orchestration a starter function scheduled, with its input as the client serialized it.</summary>
public sealed record ScheduledOrchestration(string Name, string InstanceId, string? SerializedInput, DateTimeOffset ScheduledAtUtc, DateTimeOffset? StartAt);

/// <summary>
/// The <c>[DurableClient]</c> handed to the timer trigger in tests: it records the orchestrations the
/// trigger schedules (serializing the input as the real client does) instead of starting them. The
/// test then runs them through <see cref="InProcessFunctionHost.RunScheduledAsync"/>.
/// </summary>
public sealed class RecordingDurableTaskClient(TimeProvider clock) : DurableTaskClient("in-process")
{
    private readonly Lock gate = new();
    private readonly List<ScheduledOrchestration> scheduled = [];
    private readonly DataConverter converter = JsonDataConverter.Default;

    public IReadOnlyList<ScheduledOrchestration> Scheduled
    {
        get
        {
            lock (gate)
            {
                return [.. scheduled];
            }
        }
    }

    public override Task<string> ScheduleNewOrchestrationInstanceAsync(TaskName orchestratorName, object? input = null, StartOrchestrationOptions? options = null, CancellationToken cancellation = default)
    {
        var instanceId = options?.InstanceId ?? Guid.NewGuid().ToString("N");
        lock (gate)
        {
            if (scheduled.Any(s => string.Equals(s.InstanceId, instanceId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"An orchestration with instance ID '{instanceId}' already exists.");
            }

            scheduled.Add(new ScheduledOrchestration(orchestratorName.Name, instanceId, converter.Serialize(input), clock.GetUtcNow(), options?.StartAt));
        }

        return Task.FromResult(instanceId);
    }

    public override Task<OrchestrationMetadata?> GetInstancesAsync(string instanceId, bool getInputsAndOutputs = false, CancellationToken cancellation = default)
    {
        ScheduledOrchestration? found;
        lock (gate)
        {
            found = scheduled.SingleOrDefault(s => string.Equals(s.InstanceId, instanceId, StringComparison.Ordinal));
        }

        return Task.FromResult(found is null ? null : new OrchestrationMetadata(found.Name, found.InstanceId)
        {
            RuntimeStatus = OrchestrationRuntimeStatus.Pending,
            CreatedAt = found.ScheduledAtUtc,
            LastUpdatedAt = found.ScheduledAtUtc,
            SerializedInput = getInputsAndOutputs ? found.SerializedInput : null,
        });
    }

    public override Task RaiseEventAsync(string instanceId, string eventName, object? eventPayload = null, CancellationToken cancellation = default) => throw Unsupported();

    public override Task<OrchestrationMetadata> WaitForInstanceStartAsync(string instanceId, bool getInputsAndOutputs = false, CancellationToken cancellation = default) => throw Unsupported();

    public override Task<OrchestrationMetadata> WaitForInstanceCompletionAsync(string instanceId, bool getInputsAndOutputs = false, CancellationToken cancellation = default) => throw Unsupported();

    public override Task SuspendInstanceAsync(string instanceId, string? reason = null, CancellationToken cancellation = default) => throw Unsupported();

    public override Task ResumeInstanceAsync(string instanceId, string? reason = null, CancellationToken cancellation = default) => throw Unsupported();

    public override AsyncPageable<OrchestrationMetadata> GetAllInstancesAsync(OrchestrationQuery? filter = null) => throw Unsupported();

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static NotSupportedException Unsupported() =>
        new("The recording client only schedules and looks up orchestrations; the test runs them with InProcessFunctionHost.");
}
