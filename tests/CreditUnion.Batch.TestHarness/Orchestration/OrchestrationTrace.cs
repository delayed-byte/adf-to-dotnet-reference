using Microsoft.DurableTask;

namespace CreditUnion.Batch.TestHarness.Orchestration;

public enum DurableTaskKind
{
    Activity,
    SubOrchestration,
}

/// <summary>
/// One attempt of an activity or sub-orchestration call made by an orchestrator. Sequence numbers
/// order events across all orchestrations of the host: <see cref="ScheduledSequence"/> is when the
/// orchestrator called the API, <see cref="StartedSequence"/> when the runner dispatched the work
/// (after the orchestrator yielded, as Durable does) and <see cref="CompletedSequence"/> when it ended.
/// </summary>
public sealed record TaskAttempt(
    int CallId,
    string OrchestrationName,
    string OrchestrationInstanceId,
    DurableTaskKind Kind,
    string Name,
    int Attempt,
    RetryPolicy? Policy,
    long ScheduledSequence,
    long StartedSequence,
    long CompletedSequence,
    DateTimeOffset ScheduledAtUtc,
    string? ChildInstanceId,
    TaskFailureDetails? Failure)
{
    public bool Failed => Failure is not null;
}

/// <summary>What the runner did after a failed attempt: retry after <see cref="Delay"/>, or give up (null).</summary>
public sealed record RetryDecision(int CallId, int Attempt, TimeSpan? Delay);

/// <summary>An orchestration instance the host ran (top-level or sub-orchestration).</summary>
public sealed record OrchestrationRecord(string Name, string InstanceId, string? ParentInstanceId, long StartedSequence, long CompletedSequence, bool Succeeded);

/// <summary>Everything the orchestrations did, in order; tests assert parallelism, ordering and retries on it.</summary>
public sealed class OrchestrationTrace
{
    private readonly Lock gate = new();
    private readonly List<TaskAttempt> attempts = [];
    private readonly List<RetryDecision> retries = [];
    private readonly List<OrchestrationRecord> orchestrations = [];
    private long sequence;
    private int callIds;

    public IReadOnlyList<TaskAttempt> Attempts
    {
        get
        {
            lock (gate)
            {
                return [.. attempts.OrderBy(a => a.ScheduledSequence)];
            }
        }
    }

    public IReadOnlyList<RetryDecision> RetryDecisions
    {
        get
        {
            lock (gate)
            {
                return [.. retries];
            }
        }
    }

    public IReadOnlyList<OrchestrationRecord> Orchestrations
    {
        get
        {
            lock (gate)
            {
                return [.. orchestrations.OrderBy(o => o.StartedSequence)];
            }
        }
    }

    public IReadOnlyList<TaskAttempt> FailedAttempts => [.. Attempts.Where(a => a.Failed)];

    /// <summary>Sub-orchestration attempts made by the orchestration <paramref name="instanceId"/>.</summary>
    public IReadOnlyList<TaskAttempt> SubOrchestrationsOf(string instanceId) =>
        [.. Attempts.Where(a => a.Kind == DurableTaskKind.SubOrchestration && string.Equals(a.OrchestrationInstanceId, instanceId, StringComparison.Ordinal))];

    /// <summary>All attempts of one call, in attempt order.</summary>
    public IReadOnlyList<TaskAttempt> AttemptsOf(int callId) => [.. Attempts.Where(a => a.CallId == callId).OrderBy(a => a.Attempt)];

    public RetryDecision? RetryDecisionAfter(TaskAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return RetryDecisions.SingleOrDefault(r => r.CallId == attempt.CallId && r.Attempt == attempt.Attempt);
    }

    /// <summary>Instance IDs of every orchestration started under <paramref name="instanceId"/>, including itself.</summary>
    public IReadOnlySet<string> InstanceTree(string instanceId)
    {
        var all = Orchestrations;
        var tree = new HashSet<string>(StringComparer.Ordinal) { instanceId };
        bool added;
        do
        {
            added = false;
            foreach (var o in all.Where(o => o.ParentInstanceId is { } parent && tree.Contains(parent)))
            {
                added |= tree.Add(o.InstanceId);
            }
        }
        while (added);
        return tree;
    }

    public void Clear()
    {
        lock (gate)
        {
            attempts.Clear();
            retries.Clear();
            orchestrations.Clear();
        }
    }

    internal long NextSequence() => Interlocked.Increment(ref sequence);

    internal int NewCallId() => Interlocked.Increment(ref callIds);

    internal void Add(TaskAttempt attempt)
    {
        lock (gate)
        {
            attempts.Add(attempt);
        }
    }

    internal void Add(RetryDecision decision)
    {
        lock (gate)
        {
            retries.Add(decision);
        }
    }

    internal void Add(OrchestrationRecord orchestration)
    {
        lock (gate)
        {
            orchestrations.Add(orchestration);
        }
    }
}
