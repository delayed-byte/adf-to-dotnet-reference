using System.Data;
using CreditUnion.Batch.AcceptanceTests.Harness;
using CreditUnion.Batch.AcceptanceTests.Support;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.TestData.Golden;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Adf;
using CreditUnion.Batch.TestHarness.Faults;
using CreditUnion.Batch.TestHarness.Golden;
using CreditUnion.Batch.TestHarness.Scenarios;
using CreditUnion.Batch.TestHarness.Sql;
using Microsoft.Data.SqlClient;

namespace CreditUnion.Batch.AcceptanceTests.Extract;

/// <summary>PL_ExtractKeyStone (adf/pipeline/PL_ExtractKeyStone.json): AT-01 to AT-04.</summary>
public sealed class ExtractKeyStoneTests(BatchEnvironmentFixture fixture)
{
    private static readonly string[] Owned = [PipelineNames.Extract];

    private BatchEnvironment Env => fixture.Environment;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("AT", "AT-01")]
    public async Task AT01_a_seeded_day_is_extracted_into_core_with_golden_counts_and_checksums()
    {
        var scenario = Scenario.Load("standard-day");
        scenario.GoldenTableExpectation(GoldenTables.PostedTransaction.Name).RowCount.ShouldBe(5_000);
        scenario.GoldenTableExpectation(GoldenTables.Account.Name).RowCount.ShouldBe(800);
        scenario.GoldenTableExpectation(GoldenTables.GlEntry.Name).RowCount.ShouldBe(300);
        await Env.LoadAsync(scenario, Ct);
        await using var host = BatchHost.Start(Env, scenario);
        var date = scenario.BusinessDate();
        Guid[] runIds = [Guid.NewGuid()];

        var run = await host.RunAsync(PipelineNames.Extract, date, runIds[0]);

        run.ShouldHaveSucceeded();
        await GoldenAssert.TablesMatchAsync(Env.Reporting, scenario, runIds, Owned, Ct);
        await Idempotency.RerunShouldChangeNothingAsync(host, runIds, [run], async () => [await host.RunAsync(PipelineNames.Extract, date, Guid.NewGuid())]);
    }

    [Fact]
    [Trait("AT", "AT-02")]
    public async Task AT02_extracting_the_same_business_date_twice_creates_no_duplicates()
    {
        var scenario = Scenario.Load("standard-day");
        await Env.LoadAsync(scenario, Ct);
        await using var host = BatchHost.Start(Env, scenario);
        var date = scenario.BusinessDate();
        Guid[] runIds = [Guid.NewGuid()];
        var rerunId = Guid.NewGuid();

        (await host.RunAsync(PipelineNames.Extract, date, runIds[0])).ShouldHaveSucceeded();
        var second = await host.RunAsync(PipelineNames.Extract, date, rerunId);

        second.ShouldHaveSucceeded();
        await GoldenAssert.TablesMatchAsync(Env.Reporting, scenario, runIds, Owned, Ct);
        var merges = await MergeNotesAsync(rerunId);
        merges.Keys.Order(StringComparer.Ordinal).ShouldBe([GoldenTables.Account.Name, GoldenTables.GlEntry.Name, GoldenTables.PostedTransaction.Name]);
        foreach (var (table, notes) in merges)
        {
            var staged = scenario.GoldenTableExpectation(table).RowCount;
            notes.ShouldBe($"staged={staged}; inserted=0; updated=0; unchanged={staged}", $"the second merge into {table} changes nothing");
        }
    }

    [Fact]
    [Trait("AT", "AT-03")]
    public async Task AT03_Db2_padding_nulls_max_precision_decimals_and_timestamps_are_stored_exactly_per_ADR_0005()
    {
        var scenario = Scenario.Load("extract-db2-types");
        await Env.LoadAsync(scenario, Ct);
        await using var host = BatchHost.Start(Env, scenario);
        var date = scenario.BusinessDate();
        Guid[] runIds = [Guid.NewGuid()];

        var run = await host.RunAsync(PipelineNames.Extract, date, runIds[0]);

        run.ShouldHaveSucceeded();
        await GoldenAssert.TablesMatchAsync(Env.Reporting, scenario, runIds, Owned, Ct);

        // The headline rules of ADR 0005, spelled out (the golden comparison above covers every row).
        var accounts = await Env.Reporting.SnapshotAsync(GoldenTables.Account, runIds, Ct);
        var byNumber = accounts.Rows.ToDictionary(r => r[0]!, StringComparer.Ordinal);
        string? Account(string number, string column) => byNumber[number][accounts.ColumnIndex(column)];
        Account("990000300001", "AccruedInterest").ShouldBe("99999999999999999999999.99999999", "DECIMAL(31,8) is not rounded through System.Decimal");
        Account("990000300004", "AccruedInterest").ShouldBe("12345678901234567890123.45678901");
        Account("990000300002", "ProductCode").ShouldBe(" LN01 ", "CHAR padding, leading space included, is kept");
        Account("990000300002", "Description").ShouldBe(string.Empty, "empty string stays empty");
        Account("990000300001", "Description").ShouldBeNull("NULL stays NULL");
        Account("990000300003", "Description").ShouldBe("TRAILING SPACES   ");
        Account("990000300004", "Description").ShouldBe("Café Zoë – Ω ✓ 日本", "text outside Windows-1252 survives");
        Account("990000300004", "LastMaintTs").ShouldBe("2026-03-08T02:30:00.123456", "a time in the spring-forward gap is copied, not converted");
        byNumber.ShouldNotContainKey("990000300006", "the previous day's account is not extracted");

        await Idempotency.RerunShouldChangeNothingAsync(host, runIds, [run], async () => [await host.RunAsync(PipelineNames.Extract, date, Guid.NewGuid())]);
    }

    [Fact]
    [Trait("AT", "AT-04")]
    public async Task AT04_refused_Db2_connections_are_retried_per_the_ADF_copy_policy_and_the_extract_succeeds()
    {
        var scenario = Scenario.Load("standard-day");
        await Env.LoadAsync(scenario, Ct);
        await using var proxy = FaultInjectingTcpProxy.Start(Env.KeyStone.Host, Env.KeyStone.Port);
        await using var host = BatchHost.Start(Env, scenario, Env.KeyStone.ExtractConnectionStringVia(FaultInjectingTcpProxy.Host, proxy.Port));
        var copyPolicy = AdfActivityPolicy.Read(PipelineNames.Extract, "Copy Db2 To Staging");
        var date = scenario.BusinessDate();
        Guid[] runIds = [Guid.NewGuid()];
        proxy.RefuseNextConnections(2);

        var run = await host.RunAsync(PipelineNames.Extract, date, runIds[0]);

        run.ShouldHaveSucceeded();
        proxy.RefusedConnections.ShouldBe(2, "the first two connection attempts were refused");
        host.Trace.ShouldHaveRetriedPerPolicy(copyPolicy, minFailures: 1, maxFailures: 2);
        await GoldenAssert.TablesMatchAsync(Env.Reporting, scenario, runIds, Owned, Ct);
        await Idempotency.RerunShouldChangeNothingAsync(host, runIds, [run], async () => [await host.RunAsync(PipelineNames.Extract, date, Guid.NewGuid())]);
    }

    private async Task<Dictionary<string, string>> MergeNotesAsync(Guid batchRunId)
    {
        await using var connection = await ReportingDatabase.OpenAsync(Env.Reporting.AdminConnectionString, Ct);
        await using var command = new SqlCommand(
            "SELECT ObjectName, Notes FROM ops.BatchRun WHERE BatchRunId = @id AND PipelineName = 'PL_ExtractKeyStone' AND StepName = 'MergeKeyStone'", connection);
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = batchRunId });
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            notes.Add(reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
        }

        return notes;
    }
}
