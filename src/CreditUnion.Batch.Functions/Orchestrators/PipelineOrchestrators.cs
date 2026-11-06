using CreditUnion.Batch.Core.Card;
using CreditUnion.Batch.Core.Concurrency;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Map;
using CreditUnion.Batch.Core.Pipelines;
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

/// <summary>PL_Reconciliation: input <see cref="ReconciliationInput"/>.</summary>
public static class ReconciliationOrchestrator
{
    [Function(PipelineNames.Reconciliation)]
    public static Task RunAsync([OrchestrationTrigger] TaskOrchestrationContext context) =>
        throw new NotImplementedException($"{PipelineNames.Reconciliation} is not converted yet (docs/PRD.md, Phase 7).");
}

/// <summary>PL_Master_NightlyBatch: input <see cref="NightlyBatchInput"/>; the instance ID is the batchRunId.</summary>
public static class NightlyBatchOrchestrator
{
    [Function(PipelineNames.Master)]
    public static Task RunAsync([OrchestrationTrigger] TaskOrchestrationContext context) =>
        throw new NotImplementedException($"{PipelineNames.Master} is not converted yet (docs/PRD.md, Phase 8).");
}
