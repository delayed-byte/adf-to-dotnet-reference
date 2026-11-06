using CreditUnion.Batch.AcceptanceTests.Harness;
using CreditUnion.Batch.AcceptanceTests.Support;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Scenarios;
using CreditUnion.Batch.TestHarness.Sql;
using IBM.Data.Db2;
using Microsoft.Data.SqlClient;
using Microsoft.DurableTask;

namespace CreditUnion.Batch.AcceptanceTests.Extract;

/// <summary>
/// ADR 0005, Errors: a value that cannot be copied exactly (a timestamp at hour 24, a non-ASCII CHAR value)
/// fails the extract with a data error that is not retried, names the table, the row, the masked key and the
/// column, and never shows the value or a full account number. Nothing reaches core.
/// </summary>
public sealed class ExtractDataErrorTests(BatchEnvironmentFixture fixture)
{
    private const string AccountNumber = "990000900001";

    private BatchEnvironment Env => fixture.Environment;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_timestamp_at_hour_24_fails_the_extract_with_a_masked_non_retried_data_error()
    {
        var (run, host) = await RunWithBadAccountAsync("TIMESTAMP('2026-11-04-24.00.00.000000')", "'LN0001'");
        await using var _ = host;

        run.Succeeded.ShouldBeFalse();
        var error = DataError(run);
        error.ErrorMessage.ShouldContain("stg.KsAccount");
        error.ErrorMessage.ShouldContain("column LAST_MAINT_TS");
        error.ErrorMessage.ShouldContain("(key ACCOUNT_NBR=****0001)");
        error.ErrorMessage.ShouldNotContain("24.00");
        await AssertNotRetriedNothingMergedAndNothingLoggedAsync(host);
    }

    [Fact]
    public async Task A_non_ASCII_CHAR_value_fails_the_extract_with_a_masked_non_retried_data_error()
    {
        var (run, host) = await RunWithBadAccountAsync("TIMESTAMP('2026-11-04-12.00.00.000000')", "'ÉA'");
        await using var _ = host;

        run.Succeeded.ShouldBeFalse();
        var error = DataError(run);
        error.ErrorMessage.ShouldContain("stg.KsAccount");
        error.ErrorMessage.ShouldContain("column PRODUCT_CODE");
        error.ErrorMessage.ShouldContain("non-ASCII");
        error.ErrorMessage.ShouldNotContain("É");
        await AssertNotRetriedNothingMergedAndNothingLoggedAsync(host);
    }

    private async Task<(PipelineRun Run, BatchHost Host)> RunWithBadAccountAsync(string lastMaintTs, string productCode)
    {
        var scenario = Scenario.Load("standard-day");
        await Env.LoadAsync(scenario, Ct);
        await InsertAccountAsync(lastMaintTs, productCode);
        var host = BatchHost.Start(Env, scenario);
        var run = await host.RunAsync(PipelineNames.Extract, scenario.BusinessDate(), Guid.NewGuid());
        return (run, host);
    }

    private static TaskFailureDetails DataError(PipelineRun run)
    {
        for (var failure = run.Result.Failure; failure is not null; failure = failure.InnerFailure)
        {
            if (failure.ErrorType == typeof(ExtractDataException).FullName)
            {
                return failure;
            }
        }

        throw new Shouldly.ShouldAssertException($"expected an {nameof(ExtractDataException)} in the failure, but {run.Describe()}");
    }

    private async Task AssertNotRetriedNothingMergedAndNothingLoggedAsync(BatchHost host)
    {
        var copies = host.Trace.Attempts.Where(a => a.Name == "Extract_CopyDb2ToStaging").ToList();
        copies.Where(a => a.Failed).ShouldAllBe(a => a.Attempt == 1, "a data error is not retried");
        host.Trace.Attempts.Any(a => a.Name == "Extract_MergeKeyStoneToCore").ShouldBeFalse("the merge runs only after every copy succeeded");

        await using var connection = await ReportingDatabase.OpenAsync(Env.Reporting.AdminConnectionString, Ct);
        await using var count = new SqlCommand("SELECT (SELECT COUNT(*) FROM core.Account) + (SELECT COUNT(*) FROM core.PostedTransaction) + (SELECT COUNT(*) FROM core.GlEntry)", connection);
        ((int)(await count.ExecuteScalarAsync(Ct))!).ShouldBe(0, "nothing is merged into core");

        var everything = host.Logs.Logs.SelectMany(l => l.Texts).Concat(host.Logs.Spans.SelectMany(s => s.Texts)).ToList();
        everything.Any(t => t.Contains(AccountNumber, StringComparison.Ordinal)).ShouldBeFalse("the full account number is never logged");
    }

    private async Task InsertAccountAsync(string lastMaintTs, string productCode)
    {
        await using var connection = await Env.KeyStone.OpenAsync(cancellationToken: Ct);
        await ExecuteAsync(connection,
            "INSERT INTO KS.MEMBER (MEMBER_NBR, FIRST_NAME, LAST_NAME, MEMBER_SINCE, BRANCH_CODE, STATUS, LAST_MAINT_TS) " +
            "VALUES ('9999900001', 'TEST', 'MEMBER', DATE('2020-01-01'), 'BR01', 'A', TIMESTAMP('2026-11-04-12.00.00.000000'))");
        await ExecuteAsync(connection,
            "INSERT INTO KS.ACCOUNT (ACCOUNT_NBR, MEMBER_NBR, ACCOUNT_CATEGORY, PRODUCT_CODE, DESCRIPTION, OPEN_DATE, CLOSE_DATE, CURRENT_BALANCE, AVAILABLE_BALANCE, " +
            "INTEREST_RATE, ACCRUED_INTEREST, STATUS, LAST_ACTIVITY_DATE, LAST_MAINT_TS) " +
            $"VALUES ('{AccountNumber}', '9999900001', 'S', {productCode}, NULL, DATE('2020-01-01'), NULL, 1.00, 1.00, 0.010000, 0.00000000, 'O', DATE('2026-11-04'), {lastMaintTs})");
    }

    private static async Task ExecuteAsync(DB2Connection connection, string sql)
    {
        using var command = new DB2Command(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
