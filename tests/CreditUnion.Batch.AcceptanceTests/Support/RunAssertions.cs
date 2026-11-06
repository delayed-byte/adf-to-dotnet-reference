using System.Data;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.TestData.Oracle;
using CreditUnion.Batch.TestData.Scenarios;
using CreditUnion.Batch.TestHarness.Adf;
using CreditUnion.Batch.TestHarness.Orchestration;
using CreditUnion.Batch.TestHarness.Sql;
using Microsoft.Data.SqlClient;
using Shouldly;

namespace CreditUnion.Batch.AcceptanceTests.Support;

/// <summary>Assertions on pipeline runs: outcome, error code and messages, stage order, and retries per the ADF policy.</summary>
public static class RunAssertions
{
    public static void ShouldHaveSucceeded(this PipelineRun run, string? because = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!run.Succeeded)
        {
            throw new ShouldAssertException($"{(because is null ? string.Empty : because + ": ")}expected success, but {run.Describe()}");
        }
    }

    public static void ShouldHaveFailedWith(this PipelineRun run, string errorCode)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Succeeded || run.ErrorCode != errorCode)
        {
            throw new ShouldAssertException($"expected failure with error code {errorCode} (a {nameof(PipelineFailedException)}, ADR 0008), but {run.Describe()}");
        }
    }

    /// <summary>Status, error code, KeyBridge calls, alert count and, for a failed master, the messages in its failure.</summary>
    public static void ShouldMatchOutcome(this PipelineRun run, RunExpectation expected, int? keyBridgeRequests = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Status == "Succeeded")
        {
            run.ShouldHaveSucceeded();
        }
        else
        {
            run.ShouldHaveFailedWith(expected.ErrorCode!);
            foreach (var message in expected.Messages.Where(m => m.Pipeline == run.Pipeline))
            {
                run.Result.Failure!.ErrorMessage.ShouldContain(message.Contains, Case.Sensitive, $"{run.Pipeline} failure message");
            }
        }

        run.KeyBridgeRequests.Count.ShouldBe(keyBridgeRequests ?? expected.KeyBridgeRequests, "KeyBridge mapping requests in the run");
        run.Alerts.Count.ShouldBe(expected.Alerts.Count, "alerts posted in the run");
    }

    /// <summary>Each expected message is in the ErrorMessage of the run's ops.BatchRun event for that pipeline and step.</summary>
    public static async Task ShouldHaveLoggedMessagesAsync(this ReportingDatabase database, Guid batchRunId, IEnumerable<MessageExpectation> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(messages);
        var logged = new List<(string Pipeline, string? Step, string Message)>();
        await using (var connection = await ReportingDatabase.OpenAsync(database.AdminConnectionString, cancellationToken))
        await using (var command = new SqlCommand("SELECT PipelineName, StepName, ErrorMessage FROM ops.BatchRun WHERE BatchRunId = @id AND ErrorMessage IS NOT NULL", connection))
        {
            command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = batchRunId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                logged.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)));
            }
        }

        foreach (var m in messages)
        {
            logged.Any(l => l.Pipeline == m.Pipeline && l.Step == m.StepName && l.Message.Contains(m.Contains, StringComparison.Ordinal))
                .ShouldBeTrue($"ops.BatchRun has no {m.Pipeline}/{m.StepName} event whose ErrorMessage contains '{m.Contains}'. Logged: {string.Join(" | ", logged.Select(l => $"{l.Pipeline}/{l.Step}: {l.Message}"))}");
        }
    }

    /// <summary>
    /// The master ran exactly the expected child pipelines in the expected stages: pipelines of one
    /// stage overlap (ADF runs them in parallel) and a stage starts only after the previous one ended.
    /// </summary>
    public static void ShouldHaveRunInStages(this OrchestrationTrace trace, PipelineRun master, RunExpectation expected)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(expected);
        var children = trace.SubOrchestrationsOf(master.Result.InstanceId).GroupBy(a => a.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (Scheduled: g.Min(a => a.ScheduledSequence), Started: g.Min(a => a.StartedSequence), Completed: g.Max(a => a.CompletedSequence)), StringComparer.Ordinal);

        children.Keys.Order(StringComparer.Ordinal).ShouldBe(expected.PipelinesRun.Where(p => p != PipelineNames.Master).Order(StringComparer.Ordinal),
            "child pipelines the master started (as sub-orchestrations)");
        foreach (var notRun in expected.PipelinesNotRun)
        {
            children.ShouldNotContainKey(notRun, $"{notRun} must not run");
        }

        for (var i = 0; i < expected.Stages.Count; i++)
        {
            var stage = expected.Stages[i].Select(p => (Name: p, Span: children[p])).ToList();
            if (stage.Count > 1)
            {
                stage.Max(s => s.Span.Started).ShouldBeLessThan(stage.Min(s => s.Span.Completed),
                    $"{string.Join(" and ", stage.Select(s => s.Name))} run in parallel (stage {i + 1})");
            }

            if (i > 0)
            {
                var previous = expected.Stages[i - 1];
                stage.Min(s => s.Span.Scheduled).ShouldBeGreaterThan(previous.Max(p => children[p].Completed),
                    $"{string.Join(" and ", stage.Select(s => s.Name))} start only after {string.Join(" and ", previous)} ended");
            }
        }
    }

    /// <summary>
    /// Every failed attempt in the run was retried by the Durable retry policy equivalent to
    /// <paramref name="adf"/> (same number of attempts, same fixed interval), and every retried call
    /// eventually succeeded (PRD: retries per policy; ADR 0008: ADF retries are RetryPolicy on the call).
    /// </summary>
    public static void ShouldHaveRetriedPerPolicy(this OrchestrationTrace trace, AdfActivityPolicy adf, int minFailures, int maxFailures)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(adf);
        var failed = trace.FailedAttempts;
        failed.Count.ShouldBeInRange(minFailures, maxFailures,
            $"failed attempts recorded by the orchestration runner; a fault absorbed inside an activity (driver or HTTP-client retries) is not retried per the ADF policy. Failures: {string.Join("; ", failed.Select(f => $"{f.Name} #{f.Attempt}: {f.Failure!.ErrorType}"))}");
        foreach (var attempt in failed)
        {
            var label = $"{attempt.Name} attempt {attempt.Attempt} ({attempt.Failure!.ErrorType}: {attempt.Failure.ErrorMessage})";
            adf.IsImplementedBy(attempt.Policy).ShouldBeTrue($"{label} was called with {AdfActivityPolicy.Describe(attempt.Policy)}; the ADF policy is {adf}");
            trace.RetryDecisionAfter(attempt).ShouldNotBeNull(label).Delay.ShouldBe(adf.RetryInterval, $"{label} is retried after the ADF interval");
            var all = trace.AttemptsOf(attempt.CallId);
            all[^1].Failed.ShouldBeFalse($"{attempt.Name} succeeds after {all.Count} attempts");
        }
    }

    /// <summary>A failure that is not transient (validation, unmapped codes) is not retried (PRD, Resilience).</summary>
    public static void ShouldNotHaveRetried(this OrchestrationTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var retried = trace.RetryDecisions.Where(d => d.Delay is not null).ToList();
        retried.ShouldBeEmpty($"retried calls: {string.Join("; ", retried.Select(r => trace.AttemptsOf(r.CallId)[0].Name))}");
    }
}
