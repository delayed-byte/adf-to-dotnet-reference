using CreditUnion.Batch.AcceptanceTests.Harness;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Sql;
using IBM.Data.Db2;
using Microsoft.Data.SqlClient;

namespace CreditUnion.Batch.AcceptanceTests.CrossCutting;

/// <summary>
/// AT-19 (PRD, NCUA Part 748 App. A alignment): the read-only extract login cannot write to KeyStone,
/// and the reader login cannot write to the reporting store. Each write is first run by an admin login
/// inside a rolled-back transaction, so a rejection proves a missing privilege rather than a bad
/// statement. These controls were built in Phase 1 (db/keystone-db2/migrations/0007,
/// db/reporting/security), so AT-19 passes before any pipeline is converted (ADR 0008).
/// </summary>
public sealed class LeastPrivilegeTests(BatchEnvironmentFixture fixture)
{
    // SQLSTATE 42501: the authorization ID lacks the privilege on the object; 42502: lacks the authority (e.g. CREATETAB).
    private static readonly string[] Db2AuthorizationStates = ["42501", "42502"];

    // 229: permission denied on object; 262: permission denied in database (e.g. CREATE TABLE); 1088: TRUNCATE without ALTER.
    private static readonly int[] SqlPermissionErrors = [229, 262, 1088];

    private BatchEnvironment Env => fixture.Environment;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Db2Writes =>
    [
        "INSERT INTO KS.TXN_CODE (TXN_CODE, DESCRIPTION, CATEGORY) VALUES ('ZZ19', 'AT-19 probe', 'DEP')",
        "UPDATE KS.ACCOUNT SET STATUS = STATUS WHERE 1 = 0",
        "DELETE FROM KS.POSTED_TXN WHERE 1 = 0",
        "CREATE TABLE KS.AT19_PROBE (ID INTEGER NOT NULL)",
    ];

    public static TheoryData<string> ReportingWrites =>
    [
        "INSERT INTO core.DailyGlTotals (BusinessDate, GlAccountNbr, DebitTotal, CreditTotal, NetAmount, TxnCount) VALUES ('2026-11-04', '1010', 0, 0, 0, 0)",
        "UPDATE core.Account SET Status = Status WHERE 1 = 0",
        "DELETE FROM core.PostedTransaction WHERE 1 = 0",
        "INSERT INTO ops.BatchRun (BatchRunId, BusinessDate, PipelineName, Status) VALUES (NEWID(), '2026-11-04', 'AT-19', 'Started')",
        "INSERT INTO ref.TxnCodeGlMap (TxnCode, GlAccountNbr, DrCr, AllocationRatio, RefreshedBatchRunId, RefreshedUtc) VALUES ('ZZ19', '1010', 'D', 1, NEWID(), SYSUTCDATETIME())",
        "TRUNCATE TABLE ref.TxnCodeGlMap",
        "EXEC ops.usp_LogBatchRunEvent @BatchRunId = '00000000-0000-0000-0000-000000000019', @BusinessDate = '2026-11-04', @PipelineName = 'AT-19', @Status = 'Started'",
        "CREATE TABLE core.AT19_Probe (Id int NOT NULL)",
    ];

    [Theory]
    [Trait("AT", "AT-19")]
    [MemberData(nameof(Db2Writes))]
    public async Task AT19_the_extract_login_cannot_write_to_KeyStone(string statement)
    {
        await using (var admin = await Env.KeyStone.OpenAsync(cancellationToken: Ct))
        using (var transaction = admin.BeginTransaction())
        {
            using var control = new DB2Command(statement, admin, transaction);
            await control.ExecuteNonQueryAsync(Ct);
            transaction.Rollback();
        }

        await using var extract = await Env.KeyStone.OpenAsync(Env.KeyStone.ExtractConnectionString, Ct);
        using var command = new DB2Command(statement, extract);
        var ex = await Should.ThrowAsync<DB2Exception>(() => command.ExecuteNonQueryAsync(Ct));
        ex.Errors.Cast<DB2Error>().Select(e => e.SQLState).ShouldContain(s => Db2AuthorizationStates.Contains(s), $"KSEXTRACT is refused for lack of privilege: {ex.Message}");
    }

    [Theory]
    [Trait("AT", "AT-19")]
    [MemberData(nameof(ReportingWrites))]
    public async Task AT19_the_reader_login_cannot_write_to_the_reporting_store(string statement)
    {
        // The positive control inserts real keys (e.g. 2026-11-04 / GL 1010), so earlier tests' rows must not be there.
        await Env.Reporting.ResetAsync(Ct);
        await using (var admin = await ReportingDatabase.OpenAsync(Env.Reporting.AdminConnectionString, Ct))
        await using (var transaction = (SqlTransaction)await admin.BeginTransactionAsync(Ct))
        {
            await using var control = new SqlCommand(statement, admin, transaction);
            await control.ExecuteNonQueryAsync(Ct);
            await transaction.RollbackAsync(Ct);
        }

        await using var reader = await ReportingDatabase.OpenAsync(Env.Reporting.ReaderConnectionString, Ct);
        await using var command = new SqlCommand(statement, reader);
        var ex = await Should.ThrowAsync<SqlException>(() => command.ExecuteNonQueryAsync(Ct));
        ex.Errors.Cast<SqlError>().Select(e => e.Number).ShouldContain(n => SqlPermissionErrors.Contains(n), $"cu_report_reader is refused for lack of permission: {ex.Message}");
    }
}
