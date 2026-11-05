using System.Data;
using System.Text;
using CreditUnion.Batch.TestData.Canonical;
using CreditUnion.Batch.TestData.Card;
using CreditUnion.Batch.TestData.Golden;
using CreditUnion.Batch.TestData.KeyStone;
using CreditUnion.Batch.TestData.Oracle;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Golden;
using CreditUnion.Batch.TestHarness.Scenarios;
using CreditUnion.Batch.TestHarness.Sql;
using Microsoft.Data.SqlClient;

namespace CreditUnion.Batch.AcceptanceTests.Harness;

/// <summary>
/// Harness self-tests against real containers: scenario inputs load losslessly, the golden extract
/// tables are exactly the source rows read by the ADR 0005 rules, and every golden output agrees with
/// the Phase 1 stored procedures (the part of the ADF source of truth that can run locally).
/// </summary>
public sealed class GoldenConsistencyTests(BatchEnvironmentFixture fixture)
{
    private static readonly string[] ProcedureLoggedSteps = ["MergeKeyStone", "MergeCardSettlement"];

    private BatchEnvironment Env => fixture.Environment;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Scenarios => [.. Scenario.CommittedNames];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Scenario_inputs_load_into_db2_and_blob_storage_and_read_back_exactly(string name)
    {
        var scenario = Scenario.Load(name);
        await Env.LoadAsync(scenario, Ct);

        var problems = new List<string>();
        foreach (var table in KeyStoneTables.LoadOrder)
        {
            var expected = scenario.InputTable(table);
            var actual = await Env.KeyStone.ReadAsync(table, cancellationToken: Ct);
            if (table == KeyStoneTables.TxnCode)
            {
                var added = expected.Rows.Select(r => r[0]).ToHashSet(StringComparer.Ordinal);
                actual = actual.Where(r => added.Contains(r[0]));
            }

            var comparison = TableComparison.Compare(expected, actual, table.KeyColumns);
            if (!comparison.IsMatch)
            {
                problems.Add(comparison.Report());
            }
        }

        var blobs = (await Env.Blobs.ListAsync(Ct)).Select(b => (b.Container + "/" + b.Name, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b.Content))));
        var inputs = scenario.InputBlobs().Select(b => (b.Container + "/" + b.Name, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b.Content))));
        blobs.Order().ShouldBe(inputs.Order());
        problems.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Golden_extract_tables_are_the_source_rows_of_the_run_dates_read_by_the_ADR_0005_rules(string name)
    {
        var scenario = Scenario.Load(name);
        if (!scenario.Golden.Tables.Any(t => t.Name == GoldenTables.PostedTransaction.Name))
        {
            return;
        }

        await Env.LoadAsync(scenario, Ct);
        var dates = string.Join(", ", scenario.Runs.Where(r => r.Pipeline is Pipelines.Master or Pipelines.Extract).Select(r => $"DATE('{r.BusinessDate}')").Distinct());
        var problems = new List<string>();
        foreach (var (source, dateColumn, golden) in new[]
                 {
                     (KeyStoneTables.PostedTxn, "POSTING_DATE", GoldenTables.PostedTransaction),
                     (KeyStoneTables.Account, "LAST_ACTIVITY_DATE", GoldenTables.Account),
                     (KeyStoneTables.GlEntry, "POSTING_DATE", GoldenTables.GlEntry),
                 })
        {
            var rows = await Env.KeyStone.ReadAsync(source, $"{dateColumn} IN ({dates})", Ct);
            var renamed = new TableData(golden.Name, golden.ColumnNames, rows.Rows);
            var comparison = TableComparison.Compare(scenario.GoldenTable(golden.Name), renamed, golden.KeyColumns);
            if (!comparison.IsMatch)
            {
                problems.Add(comparison.Report());
            }
        }

        problems.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Golden_outputs_agree_with_the_source_of_truth_stored_procedures(string name)
    {
        var scenario = Scenario.Load(name);
        await Env.LoadAsync(scenario, Ct);
        var runIds = scenario.Runs.Select(_ => Guid.NewGuid()).ToList();
        var replay = new ProcedureReplay(Env.Reporting, scenario, runIds);
        foreach (var run in scenario.Runs)
        {
            await replay.RunAsync(run, Ct);
        }

        var tables = await GoldenAssert.CompareTablesAsync(Env.Reporting, scenario, runIds, cancellationToken: Ct);
        var problems = replay.Problems.Concat(tables.Where(t => !t.IsMatch && t.Table != GoldenTables.BatchRun.Name).Select(t => t.Report)).ToList();

        // Only the merge procedures log to ops.BatchRun; the other events are written by the pipelines.
        var expectedLog = scenario.GoldenTable(GoldenTables.BatchRun.Name).Where("StepName", ProcedureLoggedSteps);
        var actualLog = await Env.Reporting.SnapshotAsync(GoldenTables.BatchRun, runIds, Ct);
        var log = TableComparison.Compare(expectedLog, actualLog, []);
        if (!log.IsMatch)
        {
            problems.Add(log.Report());
        }

        problems.ShouldBeEmpty();
    }

    public static TheoryData<string> MalformedSettlementFiles =>
    [
        "H,TESTPROC,20261103,0001\nD,TP1,2026-11-04,40000,12,12.555,SALE,,54,EUR\nD,TP2,20261104,400000,1234,1.00,SALE,,,USD\nD,TP2,20261104,400000,1234,1.00,SALE,,,USD\nX,junk\nT,5,9.99\n",
        "H,TESTPROC,20261104,0001\nD,TP1,20261104,400000,1234,1.00,SALE,,,USD\nT,1,2.00\n",
        "H,TESTPROC,20261104,0001\nD,TP1,20261104,400000,1234,1.00,SALE,,,USD\n",
        "D,TP1,20261104,400000,1234,1.00,SALE,,,USD\nT,1,1.00\nT,1,1.00\n",
        "H,,2026110,0001\nD,TP1,20261104,400000,1234,-5.00,BUY,\"" + new string('N', 61) + "\",5411,usd\nT,x,1.0.0\n",
        "H,TESTPROC,20261104,0001\nD,TP1,20261104,400000,1234,12.5,RFND,\"SYNTH \"\"Q\"\" CAFE, INC\",,USD\nD,TP2,20261104,400000,1234,7,SALE,ŁÓDŹ,5499,USD\nT,2,19.50\n",
    ];

    [Theory]
    [MemberData(nameof(MalformedSettlementFiles))]
    public async Task Settlement_rules_in_the_oracle_match_stg_usp_ValidateCardSettlementFile(string content)
    {
        await Env.ResetAsync(Ct);
        var date = new DateOnly(2026, 11, 4);
        var raw = SettlementValidator.Land(Encoding.UTF8.GetBytes(content));
        var port = SettlementValidator.Validate(raw, date);
        ColumnDefinition[] columns = [new("RecordType", ColumnType.NVarChar(10), true), .. Enumerable.Range(1, 9).Select(i => new ColumnDefinition($"Field{i:D2}", ColumnType.NVarChar(200), true))];
        await Env.Reporting.BulkInsertAsync("stg.CardSettlementRaw", columns,
            new TableData("raw", [.. columns.Select(c => c.Name)], raw.Select(r => (IReadOnlyList<string?>)[r.RecordType, .. r.Fields])),
            new Dictionary<string, object> { ["BusinessDate"] = date.ToDateTime(TimeOnly.MinValue), ["SourceFile"] = "f.csv" }, Ct);

        await using var connection = await ReportingDatabase.OpenAsync(Env.Reporting.AdminConnectionString, Ct);
        await using (var validate = new SqlCommand("stg.usp_ValidateCardSettlementFile", connection) { CommandType = CommandType.StoredProcedure })
        {
            validate.Parameters.Add(new SqlParameter("@BusinessDate", SqlDbType.Date) { Value = date.ToDateTime(TimeOnly.MinValue) });
            validate.Parameters.AddWithValue("@SourceFile", "f.csv");
            validate.Parameters.AddWithValue("@BatchRunId", Guid.NewGuid());
            await using var reader = await validate.ExecuteReaderAsync(Ct);
            (await reader.ReadAsync(Ct)).ShouldBeTrue();
            reader.GetBoolean(reader.GetOrdinal("IsValid")).ShouldBe(port.IsValid);
            reader.GetInt32(reader.GetOrdinal("DetailRowCount")).ShouldBe(port.DetailRowCount);
            (reader.IsDBNull(reader.GetOrdinal("ErrorSummary")) ? null : reader.GetString(reader.GetOrdinal("ErrorSummary"))).ShouldBe(port.ErrorSummary);
        }

        await using var errors = new SqlCommand("stg.usp_GetCardSettlementValidationErrors", connection) { CommandType = CommandType.StoredProcedure };
        errors.Parameters.Add(new SqlParameter("@BusinessDate", SqlDbType.Date) { Value = date.ToDateTime(TimeOnly.MinValue) });
        errors.Parameters.AddWithValue("@SourceFile", "f.csv");
        await using var rows = await errors.ExecuteReaderAsync(Ct);
        var actual = new List<string>();
        while (await rows.ReadAsync(Ct))
        {
            actual.Add($"{Text(rows, "recordType")}|{Text(rows, "processorRefNbr")}|{Text(rows, "errorCode")}|{Text(rows, "errorMessage")}");
        }

        // Compared as multisets: the procedure's ORDER BY ties when one record has two errors with the same code.
        actual.Order(StringComparer.Ordinal).ShouldBe(port.Errors.Select(e => $"{e.RecordType}|{e.ProcessorRefNbr}|{e.ErrorCode}|{e.ErrorMessage}").Order(StringComparer.Ordinal));
    }

    private static string Text(SqlDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column)) ? string.Empty : reader.GetString(reader.GetOrdinal(column));
}
