using System.Data;
using CreditUnion.Batch.AcceptanceTests.Harness;
using CreditUnion.Batch.AcceptanceTests.Support;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.TestData.Golden;
using CreditUnion.Batch.TestData.Oracle;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Golden;
using CreditUnion.Batch.TestHarness.Scenarios;
using CreditUnion.Batch.TestHarness.Sql;
using Microsoft.Data.SqlClient;

namespace CreditUnion.Batch.AcceptanceTests.CardSettlement;

/// <summary>PL_IngestCardSettlement (adf/pipeline/PL_IngestCardSettlement.json): AT-05 to AT-07.</summary>
public sealed class IngestCardSettlementTests(BatchEnvironmentFixture fixture)
{
    private static readonly string[] Owned = [PipelineNames.CardSettlement];
    private static readonly string[] CardContainers = [BatchOracle.LandingContainer, BatchOracle.ArchiveContainer, BatchOracle.QuarantineContainer];

    private BatchEnvironment Env => fixture.Environment;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("AT", "AT-05")]
    public async Task AT05_three_valid_settlement_files_are_loaded_and_archived()
    {
        var scenario = Scenario.Load("standard-day");
        await Env.LoadAsync(scenario, Ct);
        await using var host = BatchHost.Start(Env, scenario);
        var date = scenario.BusinessDate();
        Guid[] runIds = [Guid.NewGuid()];

        var run = await host.RunAsync(PipelineNames.CardSettlement, date, runIds[0]);

        run.ShouldHaveSucceeded();
        await GoldenAssert.TablesMatchAsync(Env.Reporting, scenario, runIds, Owned, Ct);
        await GoldenAssert.BlobsMatchAsync(Env.Blobs, scenario, runIds, CardContainers, Ct);
        scenario.Golden.Blobs.Count(b => b.Container == BatchOracle.ArchiveContainer && b.Name.StartsWith("2026-11-04/", StringComparison.Ordinal)).ShouldBe(3);
        scenario.Golden.Blobs.ShouldNotContain(b => b.Container == BatchOracle.QuarantineContainer);
        await Idempotency.RerunShouldChangeNothingAsync(host, runIds, [run], async () => [await host.RunAsync(PipelineNames.CardSettlement, date, Guid.NewGuid())]);
    }

    [Fact]
    [Trait("AT", "AT-06")]
    public async Task AT06_a_file_whose_trailer_count_does_not_match_is_quarantined_with_a_manifest_and_the_others_load()
    {
        var scenario = Scenario.Load("card-quarantine-trailer-mismatch");
        await Env.LoadAsync(scenario, Ct);
        await using var host = BatchHost.Start(Env, scenario);
        var date = scenario.BusinessDate();
        Guid[] runIds = [Guid.NewGuid()];

        var run = await host.RunAsync(PipelineNames.CardSettlement, date, runIds[0]);

        run.ShouldHaveSucceeded("one bad file does not fail the pipeline");
        await GoldenAssert.TablesMatchAsync(Env.Reporting, scenario, runIds, Owned, Ct);
        await GoldenAssert.BlobsMatchAsync(Env.Blobs, scenario, runIds, cancellationToken: Ct);
        var quarantined = scenario.Golden.Blobs.Where(b => b.Container == BatchOracle.QuarantineContainer).Select(b => b.Name).Order(StringComparer.Ordinal);
        quarantined.ShouldBe(["2026-11-04/settlement_002.csv", "2026-11-04/settlement_002.csv.errors.json"]);
        scenario.Golden.Blobs.ShouldContain(b => b.Container == BatchOracle.ArchiveContainer && b.Name == "2026-11-04/SETTLEMENT_003.CSV", "names match case-insensitively (ADR 0007)");
        await Idempotency.RerunShouldChangeNothingAsync(host, runIds, [run], async () => [await host.RunAsync(PipelineNames.CardSettlement, date, Guid.NewGuid())]);
    }

    [Fact]
    [Trait("AT", "AT-07")]
    public async Task AT07_an_empty_landing_folder_succeeds_with_zero_rows_and_a_no_files_note()
    {
        var scenario = Scenario.Load("card-empty-landing");
        await Env.LoadAsync(scenario, Ct);
        await using var host = BatchHost.Start(Env, scenario);
        var date = scenario.BusinessDate();
        Guid[] runIds = [Guid.NewGuid()];

        var run = await host.RunAsync(PipelineNames.CardSettlement, date, runIds[0]);

        run.ShouldHaveSucceeded();
        await GoldenAssert.TablesMatchAsync(Env.Reporting, scenario, runIds, Owned, Ct);
        await GoldenAssert.BlobsMatchAsync(Env.Blobs, scenario, runIds, cancellationToken: Ct);
        scenario.GoldenTableExpectation(GoldenTables.CardSettlement.Name).RowCount.ShouldBe(0);
        (await SucceededEventAsync(runIds[0])).ShouldBe((0L, "No settlement files found in the landing folder."));
        await Idempotency.RerunShouldChangeNothingAsync(host, runIds, [run], async () => [await host.RunAsync(PipelineNames.CardSettlement, date, Guid.NewGuid())]);
    }

    private async Task<(long? RecordCount, string? Notes)> SucceededEventAsync(Guid batchRunId)
    {
        await using var connection = await ReportingDatabase.OpenAsync(Env.Reporting.AdminConnectionString, Ct);
        await using var command = new SqlCommand(
            "SELECT RecordCount, Notes FROM ops.BatchRun WHERE BatchRunId = @id AND PipelineName = 'PL_IngestCardSettlement' AND StepName IS NULL AND Status = 'Succeeded'", connection);
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = batchRunId });
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue("ops.BatchRun records that PL_IngestCardSettlement succeeded");
        return (reader.IsDBNull(0) ? null : reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }
}
