using Google.Protobuf;
using Microsoft.DurableTask.Protobuf;

namespace CreditUnion.Batch.UnitTests.Determinism;

/// <summary>
/// The determinism check (ADR 0017): an orchestrator is deterministic when, given the same history, it returns the same actions
/// (names, inputs, instance IDs, timers, completion). Each recorded episode is executed again on its own history and compared,
/// field by field, with what the first execution returned; the final episode (the whole history) is replayed twice more.
/// </summary>
public static class ReplayCheck
{
    public static IReadOnlyList<string> Differences(ReplayBackend backend, RecordedOrchestration orchestration, int finalReplays = 2)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(orchestration);
        var problems = new List<string>();
        for (var i = 0; i < orchestration.Episodes.Count; i++)
        {
            Compare(i, orchestration.Episodes[i]);
        }

        for (var n = 0; n < finalReplays; n++)
        {
            Compare(orchestration.Episodes.Count - 1, orchestration.Episodes[^1]);
        }

        return problems;

        void Compare(int index, Episode episode)
        {
            var expected = Normalize(episode.Response);
            var actual = Normalize(backend.Execute(orchestration.Name, orchestration.InstanceId, episode.PastEvents, episode.NewEvents));
            if (!expected.Equals(actual))
            {
                problems.Add($"{orchestration.Name} ({orchestration.InstanceId}) episode {index}: the replay returned {JsonFormatter.Default.Format(actual)}; the first execution returned {JsonFormatter.Default.Format(expected)}");
            }
        }
    }

    /// <summary>
    /// Leaves out what the worker adds on its own and is not a decision of the orchestrator: trace contexts (span IDs differ on
    /// every execution while anything listens to activity sources), the completion token and stack-trace text.
    /// </summary>
    private static OrchestratorResponse Normalize(OrchestratorResponse response)
    {
        var copy = response.Clone();
        copy.OrchestrationTraceContext = null;
        copy.CompletionToken = string.Empty;
        copy.NumEventsProcessed = null;
        foreach (var action in copy.Actions)
        {
            if (action.ScheduleTask is { } task)
            {
                task.ParentTraceContext = null;
            }

            if (action.CreateSubOrchestration is { } sub)
            {
                sub.ParentTraceContext = null;
            }

            for (var failure = action.CompleteOrchestration?.FailureDetails; failure is not null; failure = failure.InnerFailure)
            {
                failure.StackTrace = null;
            }
        }

        return copy;
    }
}
