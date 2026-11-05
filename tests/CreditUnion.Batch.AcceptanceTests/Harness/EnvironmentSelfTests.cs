using CreditUnion.Batch.TestData.KeyStone;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Scenarios;
using CreditUnion.Batch.TestHarness.Sql;
using IBM.Data.Db2;
using Microsoft.Data.SqlClient;

namespace CreditUnion.Batch.AcceptanceTests.Harness;

/// <summary>Harness self-tests: the containers start, every login connects, and reset really isolates scenarios.</summary>
public sealed class EnvironmentSelfTests(BatchEnvironmentFixture fixture)
{
    private BatchEnvironment Env => fixture.Environment;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_login_connects_with_its_generated_credentials()
    {
        await using (var admin = await Env.KeyStone.OpenAsync(cancellationToken: Ct))
        await using (var extract = await Env.KeyStone.OpenAsync(Env.KeyStone.ExtractConnectionString, Ct))
        {
            (await ScalarAsync(admin, "SELECT COUNT(*) FROM KS.GL_ACCOUNT")).ShouldBe(10);
            (await ScalarAsync(extract, "SELECT COUNT(*) FROM KS.TXN_CODE")).ShouldBeGreaterThanOrEqualTo(10);
        }

        foreach (var connectionString in new[] { Env.Reporting.AdminConnectionString, Env.Reporting.WriterConnectionString, Env.Reporting.ReaderConnectionString })
        {
            await using var connection = await ReportingDatabase.OpenAsync(connectionString, Ct);
            await using var command = new SqlCommand("SELECT COUNT(*) FROM ops.BatchRun", connection);
            (await command.ExecuteScalarAsync(Ct)).ShouldNotBeNull();
        }

        (await Env.Blobs.ListAsync(Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Reset_removes_all_scenario_data_and_keeps_reference_data()
    {
        var scenario = Scenario.Load("map-unmapped-code");
        await Env.LoadAsync(scenario, Ct);
        (await Env.KeyStone.ReadAsync(KeyStoneTables.PostedTxn, cancellationToken: Ct)).RowCount.ShouldBeGreaterThan(0);
        (await Env.KeyStone.ReadAsync(KeyStoneTables.TxnCode, cancellationToken: Ct)).RowCount.ShouldBe(11, "P2PD is added by the scenario");
        await using (var sql = await ReportingDatabase.OpenAsync(Env.Reporting.AdminConnectionString, Ct))
        await using (var insert = new SqlCommand(
            "INSERT INTO ops.BatchRun (BatchRunId, BusinessDate, PipelineName, Status) VALUES (NEWID(), '2026-11-04', 'X', 'Started')", sql))
        {
            await insert.ExecuteNonQueryAsync(Ct);
        }

        await Env.ResetAsync(Ct);

        foreach (var table in KeyStoneTables.LoadOrder.Where(t => t != KeyStoneTables.TxnCode))
        {
            (await Env.KeyStone.ReadAsync(table, cancellationToken: Ct)).RowCount.ShouldBe(0, table.Name);
        }

        (await Env.KeyStone.ReadAsync(KeyStoneTables.TxnCode, cancellationToken: Ct)).RowCount.ShouldBe(10);
        (await Env.Blobs.ListAsync(Ct)).ShouldBeEmpty();
        await using var connection = await ReportingDatabase.OpenAsync(Env.Reporting.AdminConnectionString, Ct);
        foreach (var table in ReportingDatabase.RunDataTables)
        {
            await using var count = new SqlCommand($"SELECT COUNT(*) FROM {table}", connection);
            ((int)(await count.ExecuteScalarAsync(Ct))!).ShouldBe(0, table);
        }

        await using var config = new SqlCommand("SELECT (SELECT COUNT(*) FROM ops.ExtractConfig) + (SELECT COUNT(*) FROM ops.ReconSignRule)", connection);
        ((int)(await config.ExecuteScalarAsync(Ct))!).ShouldBe(7, "configuration seeded by migration 0006 survives a reset");
    }

    private static async Task<int> ScalarAsync(DB2Connection connection, string sql)
    {
        using var command = new DB2Command(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}
