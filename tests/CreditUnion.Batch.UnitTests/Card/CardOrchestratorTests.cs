using System.Text;
using System.Text.Json;
using CreditUnion.Batch.Core.Card;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.Functions.Activities;
using CreditUnion.Batch.TestHarness.Adf;
using CreditUnion.Batch.TestHarness.Orchestration;
using Microsoft.Extensions.DependencyInjection;

namespace CreditUnion.Batch.UnitTests.Card;

/// <summary>
/// PL_IngestCardSettlement as the in-process Durable host runs it (ADR 0008), with the reporting store and blob
/// store replaced by fakes: activity order, the per-file branches, parallelism bounded to batchCount, retries per
/// the ADF policy on virtual time, and what a rerun leaves behind. AT-05 to AT-07 run the same orchestrator
/// against real containers.
/// </summary>
public sealed class CardOrchestratorTests
{
    private const string Landing = "card-landing";
    private const string Archive = "card-archive";
    private const string Quarantine = "card-quarantine";
    private const string Day = "2026-11-04";

    private static readonly DateTimeOffset Start = new(2026, 11, 5, 11, 0, 0, TimeSpan.Zero);
    private static readonly PipelineRunInput Input = new(new DateOnly(2026, 11, 4), Guid.Parse("11111111-2222-3333-4444-555555555555"));
    private static readonly byte[] BadBytes = [0xEF, 0xBB, 0xBF, 0x48, 0x2C, 0xFF, 0x0A]; // BOM, "H,", an invalid UTF-8 byte, LF

    private readonly FakeCardStore store = new();
    private readonly FakeCardBlobStore blobs = new();
    private readonly VirtualClock clock = new(Start);
    private readonly InProcessFunctionHost host;

    public CardOrchestratorTests()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton<ICardSettlementStore>(store)
            .AddSingleton<ICardBlobStore>(blobs)
            .AddSingleton(CardSettlementSettings.Defaults with { RetainUntil = "2033-11-04" })
            .AddSingleton(CardActivityPolicies.Adf)
            .AddSingleton<CardSettlementSteps>()
            .BuildServiceProvider();
        host = new InProcessFunctionHost(FunctionCatalog.FromAssembly(typeof(CardSettlementActivities).Assembly), services, clock);
    }

    private void Land(string name, string content = "H,P,20261104,1\n") => blobs.Put(Landing, $"{Day}/{name}", content);

    private Task<OrchestrationResult> RunAsync() => host.RunOrchestrationAsync(PipelineNames.CardSettlement, Input);

    [Fact]
    public async Task A_valid_file_is_landed_validated_promoted_archived_deleted_and_logged_in_the_ADF_order_and_the_run_is_logged_around_it()
    {
        Land("settlement_001.csv", "H,P,20261104,1\nD,R1,20261103,400000,1234,25.00,SALE,M,5411,USD\nT,1,25.00\n");

        var result = await RunAsync();

        result.Succeeded.ShouldBeTrue(result.Describe());
        store.Calls.ShouldBe(
        [
            "log Started", "reset", "copy settlement_001.csv", "validate settlement_001.csv", "promote settlement_001.csv",
            "logfile Archived settlement_001.csv", "merge", "log Succeeded",
        ]);
        store.Raw["settlement_001.csv"].Count.ShouldBe(3);
        blobs.Exists(Landing, $"{Day}/settlement_001.csv").ShouldBeFalse("the landing file is deleted");
        blobs.Exists(Archive, $"{Day}/settlement_001.csv").ShouldBeTrue();
        blobs.Metadata(Archive, $"{Day}/settlement_001.csv").ShouldBe(new Dictionary<string, string> { ["retain_until"] = "2033-11-04", ["batch_run_id"] = Input.BatchRunId.ToString("D") });
        store.CardFileLogs.Single().ShouldBe(new LogCardFileCall(
            Input.BatchRunId, Input.BusinessDate, "settlement_001.csv", "Archived", 2, 2, 10.00m, 10.00m, null, $"{Archive}/{Day}/settlement_001.csv"));
    }

    [Fact]
    public async Task An_invalid_file_gets_a_manifest_and_is_quarantined_with_the_same_metadata_and_logged_without_amounts()
    {
        Land("settlement_002.csv", "H,P,20261104,1\nT,9,1.00\n");
        store.ValidationFor = file => new SettlementValidation(file, false, 0, 9, 0m, 1.00m, 1, "TRAILER_COUNT_MISMATCH x1");
        store.Errors = [new CardValidationErrorRow(Day, "settlement_002.csv", Input.BatchRunId.ToString("D"), "T", null, "TRAILER_COUNT_MISMATCH", "Trailer record count 9 does not match 0 detail records.")];

        var result = await RunAsync();

        result.Succeeded.ShouldBeTrue(result.Describe());
        store.Calls.ShouldBe(["log Started", "reset", "copy settlement_002.csv", "validate settlement_002.csv", "errors settlement_002.csv", "logfile Quarantined settlement_002.csv", "merge", "log Succeeded"]);
        store.Calls.ShouldNotContain(c => c.StartsWith("promote", StringComparison.Ordinal));
        blobs.Paths(Archive).ShouldBeEmpty();
        blobs.Paths(Quarantine).ShouldBe([$"{Day}/settlement_002.csv", $"{Day}/settlement_002.csv.errors.json"]);
        blobs.Metadata(Quarantine, $"{Day}/settlement_002.csv")["batch_run_id"].ShouldBe(Input.BatchRunId.ToString("D"));
        blobs.Metadata(Quarantine, $"{Day}/settlement_002.csv")["retain_until"].ShouldBe("2033-11-04");
        blobs.Exists(Landing, $"{Day}/settlement_002.csv").ShouldBeFalse();

        using var manifest = JsonDocument.Parse(blobs.Content(Quarantine, $"{Day}/settlement_002.csv.errors.json"));
        manifest.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        var entry = manifest.RootElement.EnumerateArray().Single();
        entry.GetProperty("errorCode").GetString().ShouldBe("TRAILER_COUNT_MISMATCH");
        entry.GetProperty("processorRefNbr").ValueKind.ShouldBe(JsonValueKind.Null);
        entry.GetProperty("sourceFile").GetString().ShouldBe("settlement_002.csv");
        entry.GetProperty("errorMessage").GetString()!.ShouldContain("Trailer record count 9", Case.Sensitive);

        store.CardFileLogs.Single().ShouldBe(new LogCardFileCall(
            Input.BatchRunId, Input.BusinessDate, "settlement_002.csv", "Quarantined", 0, 9, null, null, "TRAILER_COUNT_MISMATCH x1", $"{Quarantine}/{Day}/settlement_002.csv"));
    }

    [Fact]
    public async Task The_manifest_is_UTF8_without_a_byte_order_mark()
    {
        Land("settlement_002.csv");
        store.ValidationFor = file => new SettlementValidation(file, false, 0, 0, null, null, 1, "X");
        store.Errors = [new CardValidationErrorRow(Day, "settlement_002.csv", "b", "H", null, "HEADER_MISSING", "File has no header (H) record. Unknown record type 'Q'.")];

        await RunAsync();

        var bytes = blobs.Content(Quarantine, $"{Day}/settlement_002.csv.errors.json");
        bytes.Take(3).ShouldNotBe([0xEF, 0xBB, 0xBF]);
        Encoding.UTF8.GetString(bytes).ShouldContain("'Q'", Case.Sensitive, "apostrophes are not escaped");
    }

    [Fact]
    public async Task Only_files_named_settlement_star_csv_are_processed_case_insensitively_and_folders_are_skipped()
    {
        Land("settlement_001.csv");
        Land("SETTLEMENT_003.CSV");
        Land("readme.txt");
        Land("settlement_004.csv.tmp");
        Land("old/settlement_000.csv");
        blobs.Put(Landing, "2026-11-03/settlement_001.csv", "another date");

        var result = await RunAsync();

        result.Succeeded.ShouldBeTrue(result.Describe());
        blobs.Paths(Archive).ShouldBe([$"{Day}/SETTLEMENT_003.CSV", $"{Day}/settlement_001.csv"]);
        blobs.Paths(Landing).ShouldBe(["2026-11-03/settlement_001.csv", $"{Day}/old/settlement_000.csv", $"{Day}/readme.txt", $"{Day}/settlement_004.csv.tmp"]);
        var succeeded = store.Events.Single(e => e.Status == "Succeeded");
        succeeded.RecordCount.ShouldBe(2, "the filtered file count");
        succeeded.Notes.ShouldBe("2 settlement file(s) processed; see ops.CardFileLog.");
    }

    [Fact]
    public async Task A_missing_landing_folder_succeeds_with_zero_files_and_the_no_files_note()
    {
        var result = await RunAsync();

        result.Succeeded.ShouldBeTrue(result.Describe());
        store.Calls.ShouldBe(["log Started", "reset", "merge", "log Succeeded"]);
        var succeeded = store.Events.Single(e => e.Status == "Succeeded");
        (succeeded.RecordCount, succeeded.Notes).ShouldBe((0L, "No settlement files found in the landing folder."));
    }

    [Fact]
    public async Task The_events_carry_the_pipeline_name_the_run_and_the_orchestrators_clock()
    {
        Land("settlement_001.csv");

        await RunAsync();

        store.Events.ShouldAllBe(e => e.BatchRunId == Input.BatchRunId && e.BusinessDate == Input.BusinessDate && e.PipelineName == PipelineNames.CardSettlement);
        store.Events[0].StartedUtc.ShouldBe(Start.UtcDateTime, "the orchestrator's clock, not DateTime.UtcNow");
        store.Events[1].EndedUtc.ShouldBe(Start.UtcDateTime);
    }

    [Fact]
    public async Task At_most_four_files_are_in_flight_at_once()
    {
        for (var i = 1; i <= 9; i++)
        {
            Land($"settlement_{i:000}.csv");
        }

        store.Hook = (operation, _, _, token) => operation == "copy" ? Task.Delay(40, token) : Task.CompletedTask;

        var result = await RunAsync();

        result.Succeeded.ShouldBeTrue(result.Describe());
        store.MaxConcurrentLoads.ShouldBe(CardActivityPolicies.BatchCount);
        store.CardFileLogs.Count.ShouldBe(9);
    }

    [Fact]
    public async Task A_transient_failure_of_any_activity_is_retried_per_that_activitys_ADF_policy_and_the_run_still_succeeds()
    {
        Land("settlement_001.csv");
        store.Hook = (operation, _, attempt, _) =>
            operation is "reset" or "copy" or "validate" or "promote" or "logfile Archived" or "merge" && attempt == 1 ? throw new TimeoutException("sql down") : Task.CompletedTask;
        blobs.Hook = (operation, _, _) => operation == "delete" && Interlocked.Increment(ref deleteAttempts) == 1 ? throw new TimeoutException("storage down") : Task.CompletedTask;
        var policy = AdfActivityPolicy.Read(PipelineNames.CardSettlement, "Reset Card Staging");

        var result = await RunAsync();

        result.Succeeded.ShouldBeTrue(result.Describe());
        host.Trace.FailedAttempts.Count.ShouldBe(7);
        host.Trace.FailedAttempts.ShouldAllBe(a => policy.IsImplementedBy(a.Policy), "retry 2 every 30 s on every activity of this pipeline");
        host.Trace.RetryDecisions.Where(d => d.Delay is not null).Select(d => d.Delay).ShouldAllBe(d => d == policy.RetryInterval);
        store.CardFileLogs.Count.ShouldBe(1, "the retried activities did not duplicate the audit row");
        blobs.Paths(Archive).ShouldBe([$"{Day}/settlement_001.csv"]);
        blobs.Paths(Landing).ShouldBeEmpty();
        (clock.GetUtcNow() - Start).ShouldBe(TimeSpan.FromSeconds(30 * 7), "seven 30-second intervals, none waited in real time");
    }

    private int deleteAttempts;

    [Fact]
    public async Task An_activity_that_keeps_failing_stops_after_retry_plus_one_attempts_logs_no_Failed_event_and_does_not_merge()
    {
        Land("settlement_001.csv");
        store.Hook = (operation, _, _, _) => operation == "validate" ? throw new TimeoutException("sql down") : Task.CompletedTask;

        var result = await RunAsync();

        result.Succeeded.ShouldBeFalse();
        store.AttemptsFor("validate", "settlement_001.csv").ShouldBe(AdfActivityPolicy.Read(PipelineNames.CardSettlement, "Validate Settlement File").MaxNumberOfAttempts);
        store.Calls.ShouldNotContain("merge");
        store.Events.Select(e => e.Status).ShouldBe(["Started"], "the ADF pipeline has no failure branch");
        blobs.Exists(Landing, $"{Day}/settlement_001.csv").ShouldBeTrue("a failed file stays in the landing folder");
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_as_a_csv_fails_the_run_at_once_without_retries_and_without_quoting_the_data()
    {
        blobs.Put(Landing, $"{Day}/settlement_001.csv", "H,P,20261104,1\nD,R1,\"4000001234567890\n");

        var result = await RunAsync();

        result.Succeeded.ShouldBeFalse();
        result.Failure!.InnerFailure!.ErrorType.ShouldBe(typeof(CardFileDataException).FullName);
        result.Failure.InnerFailure.ErrorMessage.ShouldNotContain("4000001234567890");
        host.Trace.RetryDecisions.Where(d => d.Delay is not null).ShouldBeEmpty();
        store.AttemptsFor("copy", "settlement_001.csv").ShouldBe(1);
    }

    [Fact]
    public async Task Invalid_UTF8_bytes_do_not_fail_the_copy_they_are_replaced_and_left_to_SQL_validation()
    {
        blobs.Put(Landing, $"{Day}/settlement_001.csv", BadBytes);
        store.ValidationFor = file => new SettlementValidation(file, false, 0, null, null, null, 1, "HEADER_INVALID x1");

        var result = await RunAsync();

        result.Succeeded.ShouldBeTrue(result.Describe());
        store.Raw["settlement_001.csv"].Single()[1].ShouldBe("�");
    }

    [Fact]
    public async Task A_rerun_after_the_files_were_moved_changes_nothing_and_still_logs_the_run()
    {
        Land("settlement_001.csv");
        Land("settlement_002.csv");
        await RunAsync();
        var archived = blobs.Paths(Archive);
        var metadata = blobs.Metadata(Archive, $"{Day}/settlement_001.csv").ToDictionary();
        var fileLogs = store.CardFileLogs.Count;

        var second = await host.RunOrchestrationAsync(PipelineNames.CardSettlement, Input with { BatchRunId = Guid.NewGuid() });

        second.Succeeded.ShouldBeTrue(second.Describe());
        blobs.Paths(Archive).ShouldBe(archived);
        blobs.Metadata(Archive, $"{Day}/settlement_001.csv").ShouldBe(metadata, "the archived blob keeps the first run's batch_run_id");
        store.CardFileLogs.Count.ShouldBe(fileLogs);
        store.Events.Last().Notes.ShouldBe("No settlement files found in the landing folder.");
    }
}
