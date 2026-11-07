using System.Text.Json;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Map;
using CreditUnion.Batch.Core.Master;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.Core.Reconciliation;
using CreditUnion.Batch.Functions.Activities;
using CreditUnion.Batch.Functions.Orchestrators;
using CreditUnion.Batch.TestHarness.Adf;
using CreditUnion.Batch.TestHarness.Orchestration;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace CreditUnion.Batch.UnitTests.Master;

/// <summary>
/// PL_Master_NightlyBatch on the in-process Durable host (ADR 0008) with the real child orchestrators and fake stores
/// (ADR 0014): order and parallelism, the inputs the children get, every failure branch, the evidence export and its retries.
/// AT-14 to AT-18 run the same orchestrator against the containers.
/// </summary>
public sealed class MasterOrchestratorTests : IDisposable
{
    private static readonly Guid RunId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly DateOnly Date = new(2026, 11, 4);
    private static readonly NightlyBatchInput Input = new() { BusinessDate = Date };
    private static readonly string MasterId = RunId.ToString("D");
    private readonly MasterWorld world = new();

    public void Dispose() => world.Dispose();

    [Fact]
    public async Task The_happy_path_logs_Started_runs_Extract_and_Card_together_then_Map_then_Recon_exports_the_evidence_and_logs_Succeeded()
    {
        var result = await world.RunMasterAsync(Input, RunId);

        result.Succeeded.ShouldBeTrue(result.Describe());
        world.Store.Events.Select(e => e.Status).ShouldBe(["Started", "Succeeded"]);
        world.Store.Events[0].ShouldBe(MasterRunEvents.Started(RunId, Date, "Manual", MasterWorld.Start, MasterWorld.Start.UtcDateTime));
        world.Store.Events[1].ShouldBe(new LogBatchRunEventRequest(RunId, Date, PipelineNames.Master, "Succeeded",
            ObjectName: $"evidence/2026-11-04/batch-run-{MasterId}.json", EndedUtc: world.Clock.GetUtcNow().UtcDateTime));

        var children = world.Host.Trace.SubOrchestrationsOf(MasterId);
        children.Select(c => c.Name).ShouldBe([PipelineNames.Extract, PipelineNames.CardSettlement, PipelineNames.MapAndAggregate, PipelineNames.Reconciliation]);
        var (extract, card, map, recon) = (children[0], children[1], children[2], children[3]);
        Math.Max(extract.StartedSequence, card.StartedSequence).ShouldBeLessThan(Math.Min(extract.CompletedSequence, card.CompletedSequence), "Extract and Card overlap");
        map.ScheduledSequence.ShouldBeGreaterThan(Math.Max(extract.CompletedSequence, card.CompletedSequence), "Map starts after both");
        recon.ScheduledSequence.ShouldBeGreaterThan(map.CompletedSequence);
        var export = world.Host.Trace.Attempts.Single(a => a.Name == MasterActivityNames.ExportEvidence);
        export.ScheduledSequence.ShouldBeGreaterThan(recon.CompletedSequence);
        children.ShouldAllBe(c => c.ChildInstanceId == MasterId + ":" + c.Name, "readable, deterministic child instance IDs");
        children.ShouldAllBe(c => c.Policy == null, "the ADF Execute Pipeline activities have no retry policy");
    }

    [Fact]
    public async Task Every_child_gets_the_resolved_business_date_and_the_masters_instance_ID_as_batch_run_ID_and_Recon_the_tolerance()
    {
        (await world.RunMasterAsync(Input with { ReconTolerance = 0.05m }, RunId)).Succeeded.ShouldBeTrue();

        world.Extract.Events.ShouldAllBe(e => e.BatchRunId == RunId && e.BusinessDate == Date && e.PipelineName == PipelineNames.Extract);
        world.Card.Events.ShouldAllBe(e => e.BatchRunId == RunId && e.BusinessDate == Date);
        world.Map.Events.ShouldAllBe(e => e.BatchRunId == RunId && e.BusinessDate == Date);
        world.KeyBridge.Calls.ShouldBe([RunId]);
        world.Recon.Events.ShouldAllBe(e => e.BatchRunId == RunId && e.BusinessDate == Date);
        world.Recon.Queries.ShouldAllBe(q => q.Tolerance == 0.05m && q.BusinessDate == Date);
        world.Recon.Events.Last().Notes.ShouldBe("card difference=0.00; GL items out of tolerance=0; tolerance=0.05");
    }

    [Fact]
    public async Task The_evidence_file_is_the_procedures_rows_with_retain_until_metadata_at_the_ADF_path()
    {
        (await world.RunMasterAsync(Input with { RetainUntil = "2033-12-31" }, RunId)).Succeeded.ShouldBeTrue();

        var (content, metadata, contentType) = world.Evidence.Blobs.ShouldHaveSingleItem().Value;
        world.Evidence.Blobs.Keys.ShouldBe([$"evidence/2026-11-04/batch-run-{MasterId}.json"]);
        metadata.ShouldBe(new Dictionary<string, string> { ["retain_until"] = "2033-12-31" });
        contentType.ShouldBe("application/json");
        using var json = JsonDocument.Parse(content);
        json.RootElement.EnumerateArray().Select(e => e.GetProperty("item").GetString()).ShouldBe(["batchRunId", "core.PostedTransaction"]);
    }

    [Fact]
    public async Task A_run_without_a_business_date_takes_the_previous_Pacific_day_of_its_trigger_time_and_records_the_trigger()
    {
        var trigger = new DateTimeOffset(2026, 3, 8, 10, 0, 0, TimeSpan.Zero);

        (await world.RunMasterAsync(new NightlyBatchInput { TriggerName = "TR_Nightly_0300_Pacific", TriggerTimeUtc = trigger }, RunId)).Succeeded.ShouldBeTrue();

        world.Store.Events[0].BusinessDate.ShouldBe(new DateOnly(2026, 3, 7), "03:00 PDT on the spring-forward day processes Mar 7");
        world.Store.Events[0].Notes.ShouldBe("trigger=TR_Nightly_0300_Pacific; triggerTimeUtc=2026-03-08T10:00:00.0000000Z");
        world.Extract.Events.ShouldAllBe(e => e.BusinessDate == new DateOnly(2026, 3, 7));
    }

    [Fact]
    public async Task When_Map_fails_with_TXN_CODE_UNMAPPED_the_master_logs_Failed_at_Map_with_the_childs_code_and_Recon_does_not_run()
    {
        world.Map.Unmapped = new UnmappedTxnCodes(1, "P2PD");

        var result = await world.RunMasterAsync(Input, RunId);

        result.Succeeded.ShouldBeFalse();
        result.Failure!.ErrorType.ShouldBe(typeof(PipelineFailedException).FullName);
        result.Failure.ErrorMessage.ShouldBe("BATCH_STEP_FAILED: PL_MapAndAggregate failed: Unmapped transaction codes: P2PD");
        world.Store.Events.Select(e => e.Status).ShouldBe(["Started", "Failed"]);
        world.Store.Events[1].ShouldBe(new LogBatchRunEventRequest(RunId, Date, PipelineNames.Master, "Failed", StepName: PipelineNames.MapAndAggregate,
            ErrorCode: PipelineErrorCodes.TxnCodeUnmapped, ErrorMessage: "Unmapped transaction codes: P2PD", EndedUtc: world.Clock.GetUtcNow().UtcDateTime));
        world.Host.Trace.SubOrchestrationsOf(MasterId).Select(c => c.Name).ShouldNotContain(PipelineNames.Reconciliation);
        world.Host.Trace.SubOrchestrationsOf(MasterId).Count(c => c.Name == PipelineNames.MapAndAggregate).ShouldBe(1, "a failed child is not retried");
        world.Recon.Events.ShouldBeEmpty();
        world.Evidence.Blobs.ShouldBeEmpty("Export Run Evidence depends on Reconciliation succeeding");
        world.Logs.Logs.ShouldContain(l => l.Level == LogLevel.Error && l.Message.Contains("step PL_MapAndAggregate failed with TXN_CODE_UNMAPPED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_Extract_activity_that_fails_every_attempt_is_recorded_with_its_root_cause_and_Card_still_finishes()
    {
        world.Copier.OnCopy = (table, attempt, ct) => throw new TimeoutException("Copy Db2 To Staging exceeded its ADF timeout of 01:00:00.");

        var result = await world.RunMasterAsync(Input, RunId);

        result.Succeeded.ShouldBeFalse();
        PipelineFailedException.TryParse(result.Failure!.ErrorType, result.Failure.ErrorMessage, out var code, out var detail).ShouldBeTrue();
        code.ShouldBe(PipelineErrorCodes.BatchStepFailed);
        detail.ShouldStartWith("PL_ExtractKeyStone failed: ");
        var failed = world.Store.Events.Single(e => e.Status == "Failed");
        (failed.StepName, failed.ErrorCode).ShouldBe((PipelineNames.Extract, "TimeoutException"));
        failed.ErrorMessage.ShouldNotBeNull().ShouldContain("exceeded its ADF timeout");
        world.Card.Events.Select(e => e.Status).ShouldBe(["Started", "Succeeded"], "the parallel sibling runs to its end, as ADF's would");
        world.Host.Trace.SubOrchestrationsOf(MasterId).Select(c => c.Name).ShouldBe([PipelineNames.Extract, PipelineNames.CardSettlement]);
        result.Failure.InnerFailure.ShouldNotBeNull().InnerFailure.ShouldNotBeNull().ErrorType.ShouldBe(typeof(TaskFailedException).FullName, "the child's chain is kept under the master's failure");
    }

    [Fact]
    public async Task When_both_parallel_children_fail_both_Failed_events_are_logged_and_the_master_names_Extract()
    {
        world.Copier.OnCopy = (table, attempt, ct) => throw new ExtractDataException("Bad value in stg.KsT1 row 1, key ****0001, column A.");
        world.Card.Hook = (operation, file, attempt, ct) => operation == "reset" ? throw new InvalidOperationException("staging unavailable") : Task.CompletedTask;

        var result = await world.RunMasterAsync(Input, RunId);

        result.Failure!.ErrorMessage.ShouldStartWith("BATCH_STEP_FAILED: PL_ExtractKeyStone failed: ");
        world.Store.Events.Where(e => e.Status == "Failed").Select(e => (e.StepName, e.ErrorCode)).ShouldBe(
            [(PipelineNames.Extract, nameof(ExtractDataException)), (PipelineNames.CardSettlement, nameof(InvalidOperationException))], ignoreOrder: true);
        world.Host.Trace.SubOrchestrationsOf(MasterId).Select(c => c.Name).ShouldNotContain(PipelineNames.MapAndAggregate);
    }

    [Fact]
    public async Task If_the_Failed_event_cannot_be_logged_the_master_still_fails_with_BATCH_STEP_FAILED()
    {
        world.Map.Unmapped = new UnmappedTxnCodes(1, "P2PD");
        world.Store.FailLog = e => e.Status == "Failed" ? new InvalidOperationException("reporting store unavailable") : null;

        var result = await world.RunMasterAsync(Input, RunId);

        result.Failure!.ErrorMessage.ShouldBe("BATCH_STEP_FAILED: PL_MapAndAggregate failed: Unmapped transaction codes: P2PD", "Fail At Map depends on its log with 'Completed'");
        var logAttempts = world.Host.Trace.Attempts.Where(a => a.OrchestrationInstanceId == MasterId && a.Name == MasterActivityNames.LogEvent && a.Failed).ToList();
        logAttempts.Count.ShouldBe(AdfActivityPolicy.Read(PipelineNames.Master, "Log Failed At Map").MaxNumberOfAttempts, "the log was retried per its own policy first");
        world.Logs.Logs.ShouldContain(l => l.Level == LogLevel.Warning && l.Message.StartsWith("Log Failed At PL_MapAndAggregate failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reconciliation_alert_that_cannot_be_delivered_fails_the_batch_at_Reconciliation_and_exports_no_evidence()
    {
        world.Recon.AlertRequired = true;
        world.Alerts.Down = true;

        var result = await world.RunMasterAsync(Input, RunId);

        result.Failure!.ErrorMessage.ShouldStartWith("BATCH_STEP_FAILED: PL_Reconciliation failed: ");
        var failed = world.Store.Events.Single(e => e.Status == "Failed");
        (failed.StepName, failed.ErrorCode).ShouldBe((PipelineNames.Reconciliation, nameof(ReconAlertUnavailableException)));
        world.Alerts.Attempts.ShouldBe(4, "Post Recon Alert's own policy, inside the child");
        world.Recon.Marked.ShouldBe(0, "the exception stays unalerted, so the next run alerts it (ADR 0013)");
        world.Evidence.Blobs.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_transient_evidence_export_failure_is_retried_thirty_seconds_apart_and_writes_one_complete_file()
    {
        world.Store.FailEvidenceRead = attempt => attempt <= 2 ? new TimeoutException("query timeout") : null;

        var result = await world.RunMasterAsync(Input, RunId);

        result.Succeeded.ShouldBeTrue(result.Describe());
        var adf = AdfActivityPolicy.Read(PipelineNames.Master, "Export Run Evidence");
        var attempts = world.Host.Trace.Attempts.Where(a => a.Name == MasterActivityNames.ExportEvidence).ToList();
        attempts.Select(a => a.Failed).ShouldBe([true, true, false]);
        attempts.ShouldAllBe(a => adf.IsImplementedBy(a.Policy));
        (attempts[1].ScheduledAtUtc - attempts[0].ScheduledAtUtc).ShouldBe(adf.RetryInterval);
        world.Evidence.Writes.ShouldBe(3);
        using var json = JsonDocument.Parse(world.Evidence.Blobs.ShouldHaveSingleItem().Value.Content);
        json.RootElement.GetArrayLength().ShouldBe(2, "only the complete attempt left a file");
        world.Store.Events.Select(e => e.Status).ShouldBe(["Started", "Succeeded"]);
    }

    [Fact]
    public async Task An_evidence_export_that_keeps_failing_logs_Failed_at_ExportRunEvidence_and_never_Succeeded()
    {
        world.Store.FailEvidenceRead = _ => new TimeoutException("query timeout");

        var result = await world.RunMasterAsync(Input, RunId);

        result.Failure!.ErrorMessage.ShouldStartWith("BATCH_STEP_FAILED: Run evidence export failed: ");
        var failed = world.Store.Events.Single(e => e.Status == "Failed");
        (failed.StepName, failed.ErrorCode).ShouldBe(("ExportRunEvidence", nameof(TimeoutException)));
        world.Store.Events.ShouldNotContain(e => e.Status == "Succeeded");
        world.Evidence.Blobs.ShouldBeEmpty("no attempt completed, so no file exists");
        world.Host.Trace.Attempts.Count(a => a.Name == MasterActivityNames.ExportEvidence).ShouldBe(3);
    }

    [Fact]
    public async Task An_unusable_retainUntil_fails_the_export_at_once_without_retries()
    {
        var result = await world.RunMasterAsync(Input with { RetainUntil = "two\nlines" }, RunId);

        result.Succeeded.ShouldBeFalse();
        world.Store.Events.Single(e => e.Status == "Failed").ErrorCode.ShouldBe(nameof(RunEvidenceInputException));
        world.Host.Trace.Attempts.Count(a => a.Name == MasterActivityNames.ExportEvidence).ShouldBe(1);
        world.Store.EvidenceReads.ShouldBe(0, "the value is checked before any I/O");
    }

    [Fact]
    public async Task When_Log_Batch_Started_fails_nothing_else_runs_and_the_master_fails_with_the_activity_failure()
    {
        world.Store.FailLog = e => e.Status == "Started" ? new InvalidOperationException("reporting store unavailable") : null;

        var result = await world.RunMasterAsync(Input, RunId);

        result.Succeeded.ShouldBeFalse();
        result.Failure!.ErrorType.ShouldBe(typeof(TaskFailedException).FullName, "ADF has no failure branch for Log Batch Started");
        world.Host.Trace.SubOrchestrationsOf(MasterId).ShouldBeEmpty();
        world.Store.Events.ShouldBeEmpty();
        world.Host.Trace.Attempts.Count(a => a.Name == MasterActivityNames.LogEvent).ShouldBe(3);
    }

    [Fact]
    public async Task An_instance_ID_that_is_not_a_GUID_is_refused_before_anything_runs()
    {
        var result = await world.Host.RunOrchestrationAsync(PipelineNames.Master, Input, "not-a-guid");

        result.Succeeded.ShouldBeFalse();
        result.Failure!.ErrorMessage.ShouldContain("must be a GUID");
        world.Host.Trace.Attempts.ShouldBeEmpty();
    }

    [Fact]
    public void The_failure_chain_keeps_type_names_and_messages_outermost_first()
    {
        var details = new TaskFailureDetails("A", "outer", null, new TaskFailureDetails("B", "inner", null, null, null), null);

        NightlyBatchOrchestrator.Chain(details).ShouldBe([new FailureLink("A", "outer"), new FailureLink("B", "inner")]);
        NightlyBatchOrchestrator.ChildInstanceId("id", PipelineNames.Extract).ShouldBe("id:PL_ExtractKeyStone");
    }
}
