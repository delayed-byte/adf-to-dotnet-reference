using CreditUnion.Batch.Core.Card;
using CreditUnion.Batch.Core.Concurrency;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Map;
using CreditUnion.Batch.Core.Master;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.Core.Reconciliation;
using CreditUnion.Batch.Functions.Activities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace CreditUnion.Batch.Functions.Orchestrators;

// Public orchestration entry points (ADR 0008): one Durable orchestrator per ADF pipeline, registered
// under the pipeline's name and taking its typed input from context.GetInput<T>(). The acceptance
// tests (AT-01 to AT-19) are written against these names and inputs. Each body is replaced by the
// conversion in the build phase named in its exception.

/// <summary>
/// PL_ExtractKeyStone: input <see cref="PipelineRunInput"/>. Mirrors the ADF activities in order: log
/// Started, look up ops.ExtractConfig, copy each table Db2 to staging (at most three at once, ADF
/// <c>batchCount</c>) and log its staging row count, merge staging into core, log Succeeded.
/// The orchestrator does no I/O; every step is an activity whose retries are the ADF activity policy.
/// </summary>
public static class ExtractKeyStoneOrchestrator
{
    private static readonly ExtractActivityPolicies Policies = ExtractActivityPolicies.Adf;

    [Function(PipelineNames.Extract)]
    public static async Task RunAsync([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var input = context.GetInput<PipelineRunInput>() ?? throw new InvalidOperationException($"{PipelineNames.Extract} needs a PipelineRunInput.");
        var logOptions = AdfRetry.Options(Policies.LogEvent);

        // Log Extract Started
        await context.CallActivityAsync(ExtractActivityNames.LogEvent,
            Event(input, "Started") with { StartedUtc = context.CurrentUtcDateTime }, logOptions);

        // Lookup Extract Config
        var tables = await context.CallActivityAsync<IReadOnlyList<ExtractTableConfig>>(
            ExtractActivityNames.LookupConfig, input, AdfRetry.Options(Policies.LookupConfig));

        // For Each Source Table (batchCount 3): Copy Db2 To Staging, then Log Staging Row Count
        var copyOptions = AdfRetry.Options(Policies.CopyToStaging);
        await BoundedParallel.ForEachAsync(tables, ExtractActivityPolicies.BatchCount, async table =>
        {
            var rowsCopied = await context.CallActivityAsync<long>(
                ExtractActivityNames.CopyToStaging, new CopyTableRequest(table, input.BusinessDate), copyOptions);
            await context.CallActivityAsync(ExtractActivityNames.LogEvent,
                Event(input, "Succeeded") with { StepName = "CopyToStaging", ObjectName = table.TargetName, RecordCount = rowsCopied }, logOptions);
        });

        // Merge KeyStone To Core
        await context.CallActivityAsync(ExtractActivityNames.Merge, input, AdfRetry.Options(Policies.Merge));

        // Log Extract Succeeded
        await context.CallActivityAsync(ExtractActivityNames.LogEvent,
            Event(input, "Succeeded") with
            {
                RecordCount = tables.Count,
                Notes = "Tables extracted (see CopyToStaging and MergeKeyStone events for row counts).",
                EndedUtc = context.CurrentUtcDateTime,
            }, logOptions);
    }

    private static LogBatchRunEventRequest Event(PipelineRunInput input, string status) =>
        new(input.BatchRunId, input.BusinessDate, PipelineNames.Extract, status);
}

/// <summary>
/// PL_IngestCardSettlement: input <see cref="PipelineRunInput"/>. Mirrors the ADF activities in order: log Started,
/// reset staging, list the landing folder, filter to settlement files, then per file (at most four at once, ADF
/// <c>batchCount</c>) land it raw, validate it in SQL and either promote and archive it or write an error manifest
/// and quarantine it, then merge into core and log Succeeded. Every I/O step is its own activity with the ADF
/// activity's retry policy (ADR 0010); the pipeline has no failure branch, so a failure just throws.
/// </summary>
public static class IngestCardSettlementOrchestrator
{
    private static readonly CardActivityPolicies Policies = CardActivityPolicies.Adf;

    [Function(PipelineNames.CardSettlement)]
    public static async Task RunAsync([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var input = context.GetInput<PipelineRunInput>() ?? throw new InvalidOperationException($"{PipelineNames.CardSettlement} needs a PipelineRunInput.");
        var logOptions = AdfRetry.Options(Policies.LogEvent);

        // Log Card Ingest Started
        await context.CallActivityAsync(CardActivityNames.LogEvent,
            Event(input, "Started") with { StartedUtc = context.CurrentUtcDateTime }, logOptions);

        // Reset Card Staging
        await context.CallActivityAsync(CardActivityNames.ResetStaging, input, AdfRetry.Options(Policies.ResetStaging));

        // List Landing Files, then Filter Settlement Files (pure: no activity)
        var children = await context.CallActivityAsync<IReadOnlyList<LandingItem>>(
            CardActivityNames.ListLandingFiles, input, AdfRetry.Options(Policies.ListLandingFiles));
        var files = SettlementFileFilter.Select(children);

        // For Each Settlement File (batchCount 4)
        await BoundedParallel.ForEachAsync(files, CardActivityPolicies.BatchCount, file => ProcessFileAsync(context, input, file.Name));

        // Merge Card Settlement To Core
        await context.CallActivityAsync(CardActivityNames.Merge, input, AdfRetry.Options(Policies.Merge));

        // Log Card Ingest Succeeded
        await context.CallActivityAsync(CardActivityNames.LogEvent,
            Event(input, "Succeeded") with
            {
                RecordCount = files.Count,
                Notes = CardIngestNotes.ForFileCount(files.Count),
                EndedUtc = context.CurrentUtcDateTime,
            }, logOptions);
    }

    private static async Task ProcessFileAsync(TaskOrchestrationContext context, PipelineRunInput input, string fileName)
    {
        var file = new SettlementFileRequest(input.BusinessDate, input.BatchRunId, fileName);

        // Copy File To Raw Staging, then Validate Settlement File
        await context.CallActivityAsync<long>(CardActivityNames.CopyToRawStaging, file, AdfRetry.Options(Policies.CopyToRawStaging));
        var validation = await context.CallActivityAsync<SettlementValidation>(CardActivityNames.Validate, file, AdfRetry.Options(Policies.Validate));
        var logged = new LogCardFileRequest(file, validation);

        // If File Valid
        if (validation.IsValid)
        {
            await context.CallActivityAsync(CardActivityNames.Promote, file, AdfRetry.Options(Policies.Promote));
            await context.CallActivityAsync(CardActivityNames.Archive, file, AdfRetry.Options(Policies.Archive));
            await context.CallActivityAsync(CardActivityNames.DeleteArchived, file, AdfRetry.Options(Policies.DeleteArchived));
            await context.CallActivityAsync(CardActivityNames.LogArchived, logged, AdfRetry.Options(Policies.LogArchived));
        }
        else
        {
            await context.CallActivityAsync(CardActivityNames.WriteManifest, file, AdfRetry.Options(Policies.WriteManifest));
            await context.CallActivityAsync(CardActivityNames.Quarantine, file, AdfRetry.Options(Policies.Quarantine));
            await context.CallActivityAsync(CardActivityNames.DeleteQuarantined, file, AdfRetry.Options(Policies.DeleteQuarantined));
            await context.CallActivityAsync(CardActivityNames.LogQuarantined, logged, AdfRetry.Options(Policies.LogQuarantined));
        }
    }

    private static LogBatchRunEventRequest Event(PipelineRunInput input, string status) =>
        new(input.BatchRunId, input.BusinessDate, PipelineNames.CardSettlement, status);
}

/// <summary>
/// PL_MapAndAggregate: input <see cref="PipelineRunInput"/>. Mirrors the ADF activities in order: log Started, get the
/// GL mapping from KeyBridge, cache it in ref.TxnCodeGlMap, look up the codes posted on the business date that have no
/// mapping, then either log the Failed event and fail with TXN_CODE_UNMAPPED, or run the data flow (DF_MapAndAggregateGl)
/// into core.DailyGlTotals and log Succeeded. Every I/O step is its own activity with the ADF activity's retry policy
/// (ADR 0011, ADR 0012); the If Condition and the Fail activity are code here, so the validation failure is not retried.
/// </summary>
public static class MapAndAggregateOrchestrator
{
    private static readonly MapActivityPolicies Policies = MapActivityPolicies.Adf;

    [Function(PipelineNames.MapAndAggregate)]
    public static async Task RunAsync([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var input = context.GetInput<PipelineRunInput>() ?? throw new InvalidOperationException($"{PipelineNames.MapAndAggregate} needs a PipelineRunInput.");
        var logOptions = AdfRetry.Options(Policies.LogEvent);

        // Log Map Started
        await context.CallActivityAsync(MapActivityNames.LogEvent, Event(input, "Started") with { StartedUtc = context.CurrentUtcDateTime }, logOptions);

        // Get GL Mapping From KeyBridge (Web activity; base URL and credential from KeyBridgeOptions)
        var mapping = await context.CallActivityAsync<KeyBridgeGlMappings>(MapActivityNames.GetGlMapping, input, AdfRetry.Options(Policies.GetGlMapping));

        // Cache GL Mapping: MappingJson = @string(output.mappings), SourceVersion = output.version
        await context.CallActivityAsync(MapActivityNames.CacheGlMapping,
            new CacheGlMappingRequest(input.BatchRunId, mapping.MappingsJson, mapping.Version), AdfRetry.Options(Policies.CacheGlMapping));

        // Lookup Unmapped Txn Codes (firstRowOnly)
        var unmapped = await context.CallActivityAsync<UnmappedTxnCodes>(MapActivityNames.LookupUnmapped, input, AdfRetry.Options(Policies.LookupUnmapped));

        // If Any Txn Code Unmapped: @greater(firstRow.UnmappedCount, 0)
        if (unmapped.UnmappedCount > 0)
        {
            try
            {
                // Log Unmapped Txn Codes
                await context.CallActivityAsync(MapActivityNames.LogUnmapped, Event(input, "Failed") with
                {
                    StepName = "CheckTxnCodeMapping",
                    RecordCount = unmapped.UnmappedCount,
                    ErrorCode = PipelineErrorCodes.TxnCodeUnmapped,
                    ErrorMessage = unmapped.FailureMessage,
                }, AdfRetry.Options(Policies.LogUnmapped));
            }
            catch (TaskFailedException e)
            {
                // Fail Txn Code Unmapped depends on the log with condition "Completed": it runs whether the log succeeded or not.
                context.CreateReplaySafeLogger(typeof(MapAndAggregateOrchestrator).FullName!).LogUnmappedEventNotLogged(e.FailureDetails.ErrorType);
            }

            // Fail Txn Code Unmapped
            throw new PipelineFailedException(PipelineErrorCodes.TxnCodeUnmapped, unmapped.FailureMessage);
        }

        // Aggregate Daily GL Totals (Execute Data Flow DF_MapAndAggregateGl)
        await context.CallActivityAsync<int>(MapActivityNames.Aggregate, input, AdfRetry.Options(Policies.Aggregate));

        // Log Map Succeeded
        await context.CallActivityAsync(MapActivityNames.LogEvent,
            Event(input, "Succeeded") with { ObjectName = "core.DailyGlTotals", EndedUtc = context.CurrentUtcDateTime }, logOptions);
    }

    private static LogBatchRunEventRequest Event(PipelineRunInput input, string status) =>
        new(input.BatchRunId, input.BusinessDate, PipelineNames.MapAndAggregate, status);
}

internal static partial class MapOrchestratorLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Log Unmapped Txn Codes failed ({ErrorType}); failing with TXN_CODE_UNMAPPED anyway, as the ADF Fail activity's 'Completed' dependency does.")]
    public static partial void LogUnmappedEventNotLogged(this ILogger logger, string errorType);
}

/// <summary>
/// PL_Reconciliation: input <see cref="ReconciliationInput"/>. Mirrors the ADF activities in order: log Started; look up the
/// card totals and the GL totals (both depend only on the Started event, so they run together); record the exceptions (a
/// Lookup that writes); if an alert is still due, post it and mark it sent; log the outcome. Every I/O step is its own
/// activity with the ADF activity's retry policy (ADR 0013); the If Condition and the status and notes expressions are
/// deterministic code here. The webhook URL (a secret) is resolved inside the posting activity, never passed through Durable.
/// </summary>
public static class ReconciliationOrchestrator
{
    private static readonly ReconActivityPolicies Policies = ReconActivityPolicies.Adf;

    [Function(PipelineNames.Reconciliation)]
    public static async Task RunAsync([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var input = context.GetInput<ReconciliationInput>() ?? throw new InvalidOperationException($"{PipelineNames.Reconciliation} needs a ReconciliationInput.");
        var logOptions = AdfRetry.Options(Policies.LogEvent);

        // Log Recon Started
        await context.CallActivityAsync(ReconActivityNames.LogEvent, Event(input, "Started") with { StartedUtc = context.CurrentUtcDateTime }, logOptions);

        // Lookup Card Totals and Lookup GL Totals: both depend only on Log Recon Started
        var query = new ReconQuery(input.BusinessDate, input.Tolerance);
        var cardLookup = context.CallActivityAsync<CardReconTotals>(ReconActivityNames.LookupCardTotals, query, AdfRetry.Options(Policies.CardTotals));
        var glLookup = context.CallActivityAsync<GlReconTotals>(ReconActivityNames.LookupGlTotals, query, AdfRetry.Options(Policies.GlTotals));
        await Task.WhenAll(cardLookup, glLookup);
        var card = await cardLookup;
        var gl = await glLookup;

        // Record Recon Exceptions (a Lookup that writes; a known exception adds no row)
        var recorded = await context.CallActivityAsync<ReconExceptionResult>(
            ReconActivityNames.RecordExceptions, new RecordReconExceptionsRequest(input.BusinessDate, input.BatchRunId, input.Tolerance),
            AdfRetry.Options(Policies.RecordExceptions));

        // If Alert Required: @equals(firstRow.AlertRequired, true)
        if (recorded.AlertRequired)
        {
            // Get Alert Webhook Url is not an activity: the URL is configuration, read inside the posting activity (ADR 0013).
            // Post Recon Alert (retry 3 = four attempts)
            await context.CallActivityAsync(ReconActivityNames.PostAlert,
                new ReconAlert(input.BusinessDate, input.BatchRunId, card.Difference, gl.ItemsOutOfTolerance, recorded.ExceptionCount, recorded.AlertSummary),
                AdfRetry.Options(Policies.PostAlert));

            // Mark Recon Alert Sent
            await context.CallActivityAsync(ReconActivityNames.MarkAlertSent,
                new MarkReconAlertSentRequest(input.BusinessDate, input.BatchRunId), AdfRetry.Options(Policies.MarkAlertSent));
        }

        // Log Recon Outcome
        await context.CallActivityAsync(ReconActivityNames.LogEvent,
            Event(input, ReconFormat.Status(recorded.IsOutOfTolerance)) with
            {
                RecordCount = recorded.ExceptionCount,
                Notes = ReconFormat.Notes(card.Difference, gl.ItemsOutOfTolerance, input.Tolerance),
                EndedUtc = context.CurrentUtcDateTime,
            }, logOptions);
    }

    private static LogBatchRunEventRequest Event(ReconciliationInput input, string status) =>
        new(input.BatchRunId, input.BusinessDate, PipelineNames.Reconciliation, status);
}

/// <summary>
/// PL_Master_NightlyBatch: input <see cref="NightlyBatchInput"/>; the instance ID is the batchRunId (ADR 0008). Mirrors the ADF
/// activities and dependencies (ADR 0014): resolve the business date; log Started; run Extract and Card Settlement as parallel
/// sub-orchestrations; then Map and Aggregate; then Reconciliation; export the run evidence; log Succeeded. Each Execute
/// Pipeline and the export has a failure branch: log Failed with the step name and the child's error code, then fail with
/// BATCH_STEP_FAILED, so no later step starts. The orchestrator does no I/O and reads the time only from
/// <c>context.CurrentUtcDateTime</c>; the Execute Pipeline activities have no ADF policy, so the sub-orchestrations are not retried.
/// </summary>
public static class NightlyBatchOrchestrator
{
    private static readonly MasterActivityPolicies Policies = MasterActivityPolicies.Adf;

    [Function(PipelineNames.Master)]
    public static async Task RunAsync([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var input = context.GetInput<NightlyBatchInput>() ?? throw new InvalidOperationException($"{PipelineNames.Master} needs a NightlyBatchInput.");
        if (!Guid.TryParse(context.InstanceId, out var batchRunId))
        {
            throw new InvalidOperationException($"The instance ID of {PipelineNames.Master} is its batchRunId and must be a GUID (ADR 0008).");
        }

        // pipeline().TriggerTime: the trigger's time, or for a run started by hand the time it started.
        var startedUtc = context.CurrentUtcDateTime;
        var triggerTime = input.TriggerTimeUtc ?? new DateTimeOffset(startedUtc, TimeSpan.Zero);
        var logOptions = AdfRetry.Options(Policies.LogEvent);

        // Resolve Business Date (Set Variable resolvedBusinessDate)
        var run = new MasterRun(batchRunId, BusinessCalendar.Resolve(input.BusinessDate, triggerTime), logOptions);

        // Log Batch Started
        await context.CallActivityAsync(MasterActivityNames.LogEvent,
            MasterRunEvents.Started(batchRunId, run.BusinessDate, input.TriggerName ?? MasterRunEvents.ManualTriggerName, triggerTime, startedUtc), logOptions);

        // Execute Extract KeyStone and Execute Ingest Card Settlement: both depend only on Log Batch Started.
        var child = new PipelineRunInput(run.BusinessDate, batchRunId);
        var extract = ExecutePipelineAsync(context, run, PipelineNames.Extract, child);
        var card = ExecutePipelineAsync(context, run, PipelineNames.CardSettlement, child);
        await Task.WhenAll(extract, card);
        ThrowIfFailed(await extract, await card);

        // Execute Map And Aggregate: depends on both with "Succeeded".
        ThrowIfFailed(await ExecutePipelineAsync(context, run, PipelineNames.MapAndAggregate, child));

        // Execute Reconciliation (tolerance = reconTolerance)
        ThrowIfFailed(await ExecutePipelineAsync(context, run, PipelineNames.Reconciliation, new ReconciliationInput(run.BusinessDate, batchRunId, input.ReconTolerance)));

        // Export Run Evidence (Copy ops.usp_GetRunEvidence to evidence/{businessDate}/batch-run-{runId}.json)
        RunEvidenceExport evidence;
        try
        {
            evidence = await context.CallActivityAsync<RunEvidenceExport>(MasterActivityNames.ExportEvidence,
                new ExportRunEvidenceRequest(batchRunId, run.BusinessDate, input.RetainUntil), AdfRetry.Options(Policies.ExportEvidence));
        }
        catch (TaskFailedException e)
        {
            // Log Failed At Evidence, then Fail At Evidence
            throw (await RecordFailureAsync(context, run, MasterSteps.ExportRunEvidence, e)).ToException();
        }

        // Log Batch Succeeded
        await context.CallActivityAsync(MasterActivityNames.LogEvent,
            MasterRunEvents.Succeeded(batchRunId, run.BusinessDate, evidence.ObjectName, context.CurrentUtcDateTime), logOptions);
    }

    /// <summary>The sub-orchestration instance ID of a child: readable in the Durable instance list and deterministic under replay.</summary>
    public static string ChildInstanceId(string masterInstanceId, string pipeline) => masterInstanceId + ":" + pipeline;

    /// <summary>A Durable failure chain, outermost first: type names and messages only (ADR 0008).</summary>
    public static IReadOnlyList<FailureLink> Chain(TaskFailureDetails failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var chain = new List<FailureLink>();
        for (var link = failure; link is not null; link = link.InnerFailure)
        {
            chain.Add(new FailureLink(link.ErrorType, link.ErrorMessage));
        }

        return chain;
    }

    /// <summary>
    /// Execute Pipeline with waitOnCompletion, plus its failure branch: "Log Failed At …" runs on the Failed condition as soon as
    /// this child fails (its sibling may still be running), and "Fail At …" depends on it with "Completed". Returns the failure,
    /// so the caller decides when to fail, after a parallel sibling has finished.
    /// </summary>
    private static async Task<FailedStep?> ExecutePipelineAsync(TaskOrchestrationContext context, MasterRun run, string pipeline, object input)
    {
        try
        {
            await context.CallSubOrchestratorAsync(pipeline, input, new SubOrchestrationOptions(instanceId: ChildInstanceId(context.InstanceId, pipeline)));
            return null;
        }
        catch (TaskFailedException e)
        {
            return await RecordFailureAsync(context, run, pipeline, e);
        }
    }

    /// <summary>"Log Failed At {step}": the step name, the child's error code and its message (ADR 0007, ADR 0014).</summary>
    private static async Task<FailedStep> RecordFailureAsync(TaskOrchestrationContext context, MasterRun run, string stepName, TaskFailedException exception)
    {
        var failure = StepFailure.From(stepName, Chain(exception.FailureDetails));
        context.CreateReplaySafeLogger(typeof(NightlyBatchOrchestrator).FullName!).LogStepFailed(failure.StepName, failure.ErrorCode, run.BusinessDate);
        try
        {
            await context.CallActivityAsync(MasterActivityNames.LogEvent,
                MasterRunEvents.Failed(run.BatchRunId, run.BusinessDate, failure, context.CurrentUtcDateTime), run.LogOptions);
        }
        catch (TaskFailedException e)
        {
            // "Fail At …" depends on "Log Failed At …" with the condition "Completed": the run fails either way.
            context.CreateReplaySafeLogger(typeof(NightlyBatchOrchestrator).FullName!).LogFailedEventNotLogged(failure.StepName, e.FailureDetails.ErrorType);
        }

        return new FailedStep(failure, exception);
    }

    /// <summary>Fail At …: the first failed step in pipeline order (Extract before Card when both failed).</summary>
    private static void ThrowIfFailed(params FailedStep?[] steps)
    {
        if (steps.FirstOrDefault(s => s is not null) is { } failed)
        {
            throw failed.ToException();
        }
    }

    private sealed record MasterRun(Guid BatchRunId, DateOnly BusinessDate, TaskOptions LogOptions);

    private sealed record FailedStep(StepFailure Failure, TaskFailedException Cause)
    {
        public PipelineFailedException ToException() => new(PipelineErrorCodes.BatchStepFailed, Failure.FailMessage, Cause);
    }
}

internal static partial class MasterOrchestratorLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "PL_Master_NightlyBatch for {BusinessDate:yyyy-MM-dd}: step {StepName} failed with {ErrorCode}; no later step runs.")]
    public static partial void LogStepFailed(this ILogger logger, string stepName, string errorCode, DateOnly businessDate);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Log Failed At {StepName} failed ({ErrorType}); failing with BATCH_STEP_FAILED anyway, as the ADF Fail activity's 'Completed' dependency does.")]
    public static partial void LogFailedEventNotLogged(this ILogger logger, string stepName, string errorType);
}
