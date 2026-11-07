using System.Globalization;
using System.Text.Json.Nodes;
using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Master;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.Functions.Orchestrators;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Adf;
using Microsoft.DurableTask;

namespace CreditUnion.Batch.UnitTests.Master;

/// <summary>
/// The ADF JSON is the source of truth for retries, timeouts, dependencies and expressions (CLAUDE.md); the code copies them, and
/// these tests keep the copy honest. Every value is read from <c>adf/pipeline/PL_Master_NightlyBatch.json</c> and its datasets.
/// </summary>
public sealed class MasterPolicyTests
{
    private static readonly MasterActivityPolicies Code = MasterActivityPolicies.Adf;
    private static readonly string[] MainActivitiesWithPolicy = ["Log Batch Started", "Export Run Evidence", "Log Batch Succeeded"];

    private static readonly Dictionary<string, string> FailedLogs = new(StringComparer.Ordinal)
    {
        ["Log Failed At Extract"] = "Execute Extract KeyStone",
        ["Log Failed At Card Settlement"] = "Execute Ingest Card Settlement",
        ["Log Failed At Map"] = "Execute Map And Aggregate",
        ["Log Failed At Reconciliation"] = "Execute Reconciliation",
        ["Log Failed At Evidence"] = "Export Run Evidence",
    };

    private static readonly Dictionary<string, string> StepOf = new(StringComparer.Ordinal)
    {
        ["Execute Extract KeyStone"] = PipelineNames.Extract,
        ["Execute Ingest Card Settlement"] = PipelineNames.CardSettlement,
        ["Execute Map And Aggregate"] = PipelineNames.MapAndAggregate,
        ["Execute Reconciliation"] = PipelineNames.Reconciliation,
        ["Export Run Evidence"] = MasterSteps.ExportRunEvidence,
    };

    public static TheoryData<string, Func<MasterActivityPolicies, ActivityPolicy>> Activities
    {
        get
        {
            var data = new TheoryData<string, Func<MasterActivityPolicies, ActivityPolicy>>
            {
                { "Log Batch Started", p => p.LogEvent },
                { "Export Run Evidence", p => p.ExportEvidence },
                { "Log Batch Succeeded", p => p.LogEvent },
            };
            foreach (var name in FailedLogs.Keys)
            {
                data.Add(name, p => p.LogEvent);
            }

            return data;
        }
    }

    private static JsonNode Pipeline { get; } = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Adf, "pipeline", PipelineNames.Master + ".json")))!;

    private static List<JsonObject> All => [.. Pipeline["properties"]!["activities"]!.AsArray().OfType<JsonObject>()];

    [Theory]
    [MemberData(nameof(Activities))]
    public void The_code_policy_equals_the_ADF_activity_policy(string activity, Func<MasterActivityPolicies, ActivityPolicy> select)
    {
        var adf = AdfActivityPolicy.Read(PipelineNames.Master, activity);
        var code = select(Code);

        (code.Retry, code.RetryInterval, code.Timeout).ShouldBe((adf.Retry, adf.RetryInterval, adf.Timeout), activity);
        adf.IsImplementedBy(AdfRetry.Options(code).Retry!.Policy).ShouldBeTrue(activity);
    }

    [Fact]
    public void Every_ADF_activity_that_has_a_policy_is_in_the_table_and_no_Execute_Pipeline_has_one()
    {
        All.Where(a => a["policy"] is not null).Select(Name).Order(StringComparer.Ordinal)
            .ShouldBe(MainActivitiesWithPolicy.Concat(FailedLogs.Keys).Order(StringComparer.Ordinal),
                "a new or renamed ADF activity needs a policy entry in MasterActivityPolicies and a row here");
        All.Where(a => Type(a) == "ExecutePipeline").Select(Name).ShouldBe(StepOf.Keys.Take(4));
        All.Where(a => Type(a) == "ExecutePipeline").ShouldAllBe(a => a["policy"] == null, "no retry policy, so the sub-orchestration calls have none (ADR 0014)");
        All.Where(a => Type(a) == "ExecutePipeline").ShouldAllBe(a => a["typeProperties"]!["waitOnCompletion"]!.GetValue<bool>());
        TimeSpan.Parse(Find("Export Run Evidence")["typeProperties"]!["source"]!["queryTimeout"]!.GetValue<string>(), CultureInfo.InvariantCulture)
            .ShouldBe(MasterActivityPolicies.EvidenceQueryTimeout);
    }

    [Fact]
    public void The_orchestrator_mirrors_the_ADF_activities_and_dependencies()
    {
        All.Select(Name).Take(8).ShouldBe(
        [
            "Resolve Business Date", "Log Batch Started", "Execute Extract KeyStone", "Execute Ingest Card Settlement",
            "Execute Map And Aggregate", "Execute Reconciliation", "Export Run Evidence", "Log Batch Succeeded",
        ]);
        Dependencies("Resolve Business Date").ShouldBeEmpty();
        Dependencies("Log Batch Started").ShouldBe([("Resolve Business Date", "Succeeded")]);
        Dependencies("Execute Extract KeyStone").ShouldBe([("Log Batch Started", "Succeeded")], "Extract and Card depend only on the Started event, so they run in parallel");
        Dependencies("Execute Ingest Card Settlement").ShouldBe([("Log Batch Started", "Succeeded")]);
        Dependencies("Execute Map And Aggregate").ShouldBe([("Execute Extract KeyStone", "Succeeded"), ("Execute Ingest Card Settlement", "Succeeded")]);
        Dependencies("Execute Reconciliation").ShouldBe([("Execute Map And Aggregate", "Succeeded")]);
        Dependencies("Export Run Evidence").ShouldBe([("Execute Reconciliation", "Succeeded")]);
        Dependencies("Log Batch Succeeded").ShouldBe([("Export Run Evidence", "Succeeded")]);

        foreach (var (log, step) in FailedLogs)
        {
            Dependencies(log).ShouldBe([(step, "Failed")], $"{log} runs only when {step} failed");
            var fail = All.Single(a => Type(a) == "Fail" && Dependencies(Name(a)).Any(d => d.Activity == log));
            Dependencies(Name(fail)).ShouldBe([(log, "Completed")], $"{Name(fail)} runs whether or not the Failed event was logged");
        }

        All.Count.ShouldBe(8 + (FailedLogs.Count * 2), "the eight main activities and a Log and a Fail per failure branch");
        All.Where(a => Type(a) == "Fail").Select(Name).ShouldBe(["Fail At Extract", "Fail At Card Settlement", "Fail At Map", "Fail At Reconciliation", "Fail At Evidence"]);
    }

    [Fact]
    public void Each_child_gets_the_resolved_business_date_and_the_master_run_id_and_Reconciliation_the_tolerance()
    {
        foreach (var name in StepOf.Keys.Take(4))
        {
            var execute = Find(name)["typeProperties"]!;
            execute["pipeline"]!["referenceName"]!.GetValue<string>().ShouldBe(StepOf[name]);
            var parameters = execute["parameters"]!.AsObject();
            parameters["businessDate"]!["value"]!.GetValue<string>().ShouldBe("@variables('resolvedBusinessDate')");
            parameters["batchRunId"]!["value"]!.GetValue<string>().ShouldBe("@pipeline().RunId", "the children's BatchRunId is the master's instance ID");
            parameters.Select(p => p.Key).ShouldBe(StepOf[name] == PipelineNames.Reconciliation ? ["businessDate", "batchRunId", "tolerance"] : ["businessDate", "batchRunId"]);
        }

        Find("Execute Reconciliation")["typeProperties"]!["parameters"]!["tolerance"]!["value"]!.GetValue<string>().ShouldBe("@pipeline().parameters.reconTolerance");
    }

    [Fact]
    public void The_parameters_and_their_defaults_are_the_NightlyBatchInput_defaults()
    {
        var parameters = Pipeline["properties"]!["parameters"]!.AsObject();
        parameters.Select(p => p.Key).ShouldBe(["businessDate", "reconTolerance", "retainUntil"]);
        parameters["businessDate"]!["defaultValue"]!.GetValue<string>().ShouldBe(string.Empty, "empty means: derive it from the trigger time");
        decimal.Parse(parameters["reconTolerance"]!["defaultValue"]!.GetValue<string>(), NumberStyles.Number, CultureInfo.InvariantCulture).ShouldBe(new NightlyBatchInput().ReconTolerance);
        new NightlyBatchInput().ReconTolerance.ToString(CultureInfo.InvariantCulture).ShouldBe(parameters["reconTolerance"]!["defaultValue"]!.GetValue<string>());
        parameters["retainUntil"]!["defaultValue"]!.GetValue<string>().ShouldBe(new NightlyBatchInput().RetainUntil);
        Find("Resolve Business Date")["typeProperties"]!["value"]!["value"]!.GetValue<string>().ShouldBe(
            "@if(empty(pipeline().parameters.businessDate), formatDateTime(addDays(convertFromUtc(pipeline().TriggerTime, 'Pacific Standard Time'), -1), 'yyyy-MM-dd'), pipeline().parameters.businessDate)",
            "BusinessCalendar.Resolve implements this expression");
    }

    [Fact]
    public void The_Started_and_Succeeded_events_are_the_ADF_expressions()
    {
        var started = Parameters("Log Batch Started");
        started.Select(p => p.Key).ShouldBe(["BatchRunId", "BusinessDate", "PipelineName", "Status", "Notes", "StartedUtc"]);
        started["Status"]!["value"]!.GetValue<string>().ShouldBe("Started");
        started["Notes"]!["value"]!["value"]!.GetValue<string>().ShouldBe("@concat('trigger=', pipeline().TriggerName, '; triggerTimeUtc=', string(pipeline().TriggerTime))");
        MasterRunEvents.StartedNotes("T", new DateTimeOffset(2026, 11, 5, 11, 0, 0, TimeSpan.Zero)).ShouldBe("trigger=T; triggerTimeUtc=2026-11-05T11:00:00.0000000Z");
        started["StartedUtc"]!["value"]!["value"]!.GetValue<string>().ShouldBe("@utcNow()");
        started["PipelineName"]!["value"]!["value"]!.GetValue<string>().ShouldBe("@pipeline().Pipeline");

        var succeeded = Parameters("Log Batch Succeeded");
        succeeded.Select(p => p.Key).ShouldBe(["BatchRunId", "BusinessDate", "PipelineName", "Status", "ObjectName", "EndedUtc"]);
        succeeded["Status"]!["value"]!.GetValue<string>().ShouldBe("Succeeded");
        succeeded["ObjectName"]!["value"]!["value"]!.GetValue<string>()
            .ShouldBe("@concat('evidence/', variables('resolvedBusinessDate'), '/batch-run-', pipeline().RunId, '.json')");
        RunEvidenceFile.ObjectName(new BlobStorageOptions().EvidenceContainer, new DateOnly(2026, 11, 4), Guid.Empty)
            .ShouldBe("evidence/2026-11-04/batch-run-00000000-0000-0000-0000-000000000000.json");
    }

    [Fact]
    public void Each_failure_branch_logs_Failed_with_the_step_name_and_the_childs_error_and_fails_with_BATCH_STEP_FAILED()
    {
        foreach (var (log, step) in FailedLogs)
        {
            var parameters = Parameters(log);
            parameters.Select(p => p.Key).ShouldBe(["BatchRunId", "BusinessDate", "PipelineName", "Status", "StepName", "ErrorCode", "ErrorMessage", "EndedUtc"], log);
            parameters["Status"]!["value"]!.GetValue<string>().ShouldBe("Failed");
            parameters["StepName"]!["value"]!.GetValue<string>().ShouldBe(StepOf[step], $"{log}: the StepName literal is what the code writes");
            parameters["ErrorCode"]!["value"]!["value"]!.GetValue<string>().ShouldBe($"@activity('{step}').error.errorCode");
            parameters["ErrorMessage"]!["value"]!["value"]!.GetValue<string>().ShouldBe($"@activity('{step}').error.message");

            var fail = All.Single(a => Type(a) == "Fail" && Dependencies(Name(a)).Any(d => d.Activity == log))["typeProperties"]!;
            fail["errorCode"]!.GetValue<string>().ShouldBe(PipelineErrorCodes.BatchStepFailed);
            fail["message"]!["value"]!.GetValue<string>().ShouldBe($"@concat('{MasterSteps.FailMessagePrefix(StepOf[step])}', activity('{step}').error.message)");
        }
    }

    [Fact]
    public void The_evidence_copy_reads_the_procedure_and_writes_the_JSON_dataset_with_retain_until_metadata()
    {
        var copy = Find("Export Run Evidence");
        var properties = copy["typeProperties"]!;
        properties["source"]!["sqlReaderStoredProcedureName"]!.GetValue<string>().ShouldBe("[ops].[usp_GetRunEvidence]");
        properties["source"]!["storedProcedureParameters"]!.AsObject().Select(p => p.Key).ShouldBe(["BatchRunId", "BusinessDate"]);
        properties["source"]!["storedProcedureParameters"]!["BatchRunId"]!["value"]!["value"]!.GetValue<string>().ShouldBe("@pipeline().RunId");
        var sink = properties["sink"]!;
        sink["type"]!.GetValue<string>().ShouldBe("JsonSink");
        sink["formatSettings"]!["filePattern"]!.GetValue<string>().ShouldBe("arrayOfObjects");
        var metadata = sink["storeSettings"]!["metadata"]!.AsArray().ShouldHaveSingleItem()!;
        metadata["name"]!.GetValue<string>().ShouldBe(RunEvidenceFile.RetainUntilMetadata);
        metadata["value"]!["value"]!.GetValue<string>().ShouldBe("@pipeline().parameters.retainUntil");

        var output = copy["outputs"]!.AsArray().ShouldHaveSingleItem()!;
        output["referenceName"]!.GetValue<string>().ShouldBe("DS_Blob_JsonFile");
        output["parameters"]!["container"]!.GetValue<string>().ShouldBe(new BlobStorageOptions().EvidenceContainer);
        output["parameters"]!["folderPath"]!["value"]!.GetValue<string>().ShouldBe("@variables('resolvedBusinessDate')");
        output["parameters"]!["fileName"]!["value"]!.GetValue<string>().ShouldBe("@concat('batch-run-', pipeline().RunId, '.json')");
        var dataset = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Adf, "dataset", "DS_Blob_JsonFile.json")))!["properties"]!;
        dataset["typeProperties"]!["encodingName"]!.GetValue<string>().ShouldBe("UTF-8");
        dataset["type"]!.GetValue<string>().ShouldBe("Json");
    }

    [Fact]
    public void Only_an_unusable_retainUntil_is_not_retried()
    {
        AdfRetry.IsRetriable(new TaskFailureDetails(typeof(RunEvidenceInputException).FullName!, "m", null, null, null)).ShouldBeFalse();
        AdfRetry.IsRetriable(new TaskFailureDetails("Azure.RequestFailedException", "m", null, null, null)).ShouldBeTrue();
        AdfRetry.IsRetriable(new TaskFailureDetails(typeof(TimeoutException).FullName!, "m", null, null, null)).ShouldBeTrue();
    }

    [Fact]
    public void The_Functions_host_never_traces_orchestration_inputs_outputs_or_replays()
    {
        var host = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "src", "CreditUnion.Batch.Functions", "host.json")))!;
        var tracing = host["extensions"]!["durableTask"]!["tracing"]!;

        tracing["traceInputsAndOutputs"]!.GetValue<bool>().ShouldBeFalse("activity inputs carry the alert summary and the mapping JSON (ADR 0014)");
        tracing["traceReplayEvents"]!.GetValue<bool>().ShouldBeFalse();
    }

    private static JsonObject Find(string name) => All.Single(a => Name(a) == name);

    private static JsonObject Parameters(string name) => Find(name)["typeProperties"]!["storedProcedureParameters"]!.AsObject();

    private static string Name(JsonNode activity) => activity["name"]!.GetValue<string>();

    private static string Type(JsonNode activity) => activity["type"]!.GetValue<string>();

    private static List<(string Activity, string Condition)> Dependencies(string name) =>
        [.. Find(name)["dependsOn"]!.AsArray().SelectMany(d => d!["dependencyConditions"]!.AsArray().Select(c => (d["activity"]!.GetValue<string>(), c!.GetValue<string>())))];
}
