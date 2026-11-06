using System.Globalization;
using System.Text;
using System.Text.Json;
using CreditUnion.Batch.AcceptanceTests.Harness;
using CreditUnion.Batch.AcceptanceTests.Support;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.Core.Reconciliation;
using CreditUnion.Batch.TestData.Canonical;
using CreditUnion.Batch.TestData.Golden;
using CreditUnion.Batch.TestData.KeyStone;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Golden;
using CreditUnion.Batch.TestHarness.Logging;
using CreditUnion.Batch.TestHarness.Scenarios;
using CreditUnion.Batch.TestHarness.Sql;
using CreditUnion.Batch.TestHarness.Stubs;
using Microsoft.Data.SqlClient;

namespace CreditUnion.Batch.AcceptanceTests.Reconciliation;

/// <summary>
/// Conversion checks for PL_Reconciliation beyond AT-12 and AT-13 (ADR 0013), against the real containers: the alert's
/// delivery semantics (a webhook that is down, a rejected request), the GL check and the tolerance, an empty date, and the
/// AT-17 rules applied to Reconciliation runs (the AT-17 cases themselves need the Phase 8 master).
/// </summary>
public sealed class ReconciliationConversionTests(BatchEnvironmentFixture fixture)
{
    private BatchEnvironment Env => fixture.Environment;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Scenarios => ["standard-day", "recon-card-off-by-one-cent"];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Reconciliation_runs_log_no_secret_no_webhook_URL_and_no_full_member_account_or_card_number(string name)
    {
        var (host, scenario, date, runId) = await StartAsync(name);
        await using var _ = host;
        host.Webhook.FailNextRequests(1, 503);

        var run = await host.RunAsync(PipelineNames.Reconciliation, date, runId);
        var rerun = await host.RunAsync(PipelineNames.Reconciliation, date, Guid.NewGuid());

        run.ShouldHaveSucceeded();
        rerun.ShouldHaveSucceeded();
        if (name == "standard-day")
        {
            run.Alerts.ShouldBeEmpty();
        }
        else
        {
            run.Alerts.Count.ShouldBe(2, "one 503, then the retry that the webhook accepted");
            host.Logs.Logs.ShouldContain(l => l.Category.Contains("HttpReconAlertClient", StringComparison.Ordinal), "the webhook client logs; an empty log would pass vacuously");
            host.Logs.Spans.ShouldNotContain(s => s.Source == "System.Net.Http" && s.Texts.Any(t => t.Contains(host.Webhook.Url, StringComparison.OrdinalIgnoreCase)), "no HTTP-client span records the URL");
        }

        var findings = host.Logs.Findings(Secrets(host), FullNumbers(scenario));
        findings.ShouldBeEmpty($"{findings.Count} log line(s) or span(s) expose sensitive values");
    }

    [Fact]
    public async Task A_webhook_that_is_down_fails_the_run_after_four_attempts_and_leaves_the_exception_unalerted_until_a_rerun_alerts_it_once()
    {
        var (host, scenario, date, firstRun) = await StartAsync("recon-card-off-by-one-cent");
        await using var _ = host;
        host.Webhook.FailNextRequests(4, 503);

        var failed = await host.RunAsync(PipelineNames.Reconciliation, date, firstRun);

        failed.Succeeded.ShouldBeFalse();
        failed.Result.Failure!.InnerFailure!.ErrorType.ShouldBe(typeof(ReconAlertUnavailableException).FullName);
        failed.Alerts.Count.ShouldBe(4, "retry 3 = four attempts, each of which reached the webhook");
        var post = host.Trace.Attempts.Where(a => a.Name == "Recon_PostReconAlert").ToList();
        post.Count.ShouldBe(4);
        post.Take(3).ShouldAllBe(a => host.Trace.RetryDecisionAfter(a)!.Delay == TimeSpan.FromSeconds(30));
        (await ExceptionsAsync(date)).ShouldBe([new ExceptionRow("CardSettlement", "ALL", 0.01m, firstRun, null, null)], "the exception is recorded but not marked alerted");
        (await StatusesAsync(firstRun)).ShouldBe(["Started"], "the Outcome event is not logged when the If activity fails");

        var rerunId = Guid.NewGuid();
        var rerun = await host.RunAsync(PipelineNames.Reconciliation, date, rerunId);

        rerun.ShouldHaveSucceeded();
        rerun.Alerts.Count.ShouldBe(1, "the exception was never alerted, so the rerun alerts it, once");
        GoldenAssert.AlertDifferences(scenario, 1, rerun.Alerts, new RunTokens([rerunId])).ShouldBeEmpty();
        var rows = await ExceptionsAsync(date);
        rows.Count.ShouldBe(1, "the rerun does not add a second row");
        (rows[0].DetectedRun, rows[0].AlertRun, rows[0].AlertSentUtc.HasValue).ShouldBe((firstRun, rerunId, true));
        var third = await host.RunAsync(PipelineNames.Reconciliation, date, Guid.NewGuid());
        third.ShouldHaveSucceeded();
        third.Alerts.ShouldBeEmpty("and now it is silent");
    }

    [Fact]
    public async Task A_rejected_alert_is_not_retried_leaves_the_exception_unalerted_and_names_no_URL()
    {
        var (host, _, date, runId) = await StartAsync("recon-card-off-by-one-cent");
        await using var _ = host;
        host.Webhook.FailNextRequests(1, 404);

        var run = await host.RunAsync(PipelineNames.Reconciliation, date, runId);

        run.Succeeded.ShouldBeFalse();
        run.Result.Failure!.InnerFailure!.ErrorType.ShouldBe(typeof(ReconAlertRejectedException).FullName);
        run.Result.Failure.InnerFailure.ErrorMessage.ShouldContain("404");
        run.Alerts.Count.ShouldBe(1);
        host.Trace.ShouldNotHaveRetried();
        (await ExceptionsAsync(date)).ShouldHaveSingleItem().AlertSentUtc.ShouldBeNull();
        host.Logs.Findings(Secrets(host)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_GL_difference_beyond_the_tolerance_is_an_exception_and_one_alert_and_within_the_tolerance_it_is_Reconciled()
    {
        var (host, _, date, runId) = await StartAsync("standard-day");
        await using var _ = host;
        await NudgeDailyGlTotalAsync(date, 0.01m);

        var run = await host.RunAsync(PipelineNames.Reconciliation, date, runId, tolerance: 0.00m);

        run.ShouldHaveSucceeded();
        var exception = (await ExceptionsAsync(date)).ShouldHaveSingleItem();
        (exception.CheckName, exception.Difference).ShouldBe(("GeneralLedger", 0.01m));
        exception.ItemKey.ShouldEndWith(":DEBIT");
        run.Alerts.Count.ShouldBe(1);
        using (var body = JsonDocument.Parse(run.Alerts[0]))
        {
            body.RootElement.GetProperty("glItemsOutOfTolerance").GetInt32().ShouldBe(1);
            body.RootElement.GetProperty("exceptionCount").GetInt32().ShouldBe(1);
            body.RootElement.GetProperty("cardDifference").GetDecimal().ShouldBe(0.00m);
            body.RootElement.GetProperty("summary").GetString().ShouldBe($"GeneralLedger {exception.ItemKey} difference 0.01");
        }

        (await NotesAsync(runId)).ShouldBe("card difference=0.00; GL items out of tolerance=1; tolerance=0.00");
        (await StatusesAsync(runId)).ShouldBe(["Started", "Exceptions"]);
    }

    [Fact]
    public async Task A_difference_equal_to_the_tolerance_is_within_it_so_the_run_is_Reconciled_with_no_exception_and_no_alert()
    {
        var (host, _, date, runId) = await StartAsync("standard-day");
        await using var _ = host;
        await NudgeDailyGlTotalAsync(date, 0.01m);

        var run = await host.RunAsync(PipelineNames.Reconciliation, date, runId, tolerance: 0.01m);

        run.ShouldHaveSucceeded();
        run.Alerts.ShouldBeEmpty();
        (await ExceptionsAsync(date)).ShouldBeEmpty("ABS(difference) > tolerance is false at equality");
        (await StatusesAsync(runId)).ShouldBe(["Started", "Reconciled"]);
        (await NotesAsync(runId)).ShouldBe("card difference=0.00; GL items out of tolerance=0; tolerance=0.01");
    }

    [Fact]
    public async Task A_date_with_nothing_to_compare_is_Reconciled_and_the_missing_GL_count_reads_as_zero()
    {
        var (host, _, _, runId) = await StartAsync("standard-day");
        await using var _ = host;
        var empty = new DateOnly(2000, 1, 3);

        var run = await host.RunAsync(PipelineNames.Reconciliation, empty, runId);

        run.ShouldHaveSucceeded();
        run.Alerts.ShouldBeEmpty();
        (await StatusesAsync(runId)).ShouldBe(["Started", "Reconciled"]);
        (await NotesAsync(runId)).ShouldBe("card difference=0.00; GL items out of tolerance=0; tolerance=0.00",
            "SUM over no rows is NULL in SQL; ADF would print an empty string here, the code prints 0 (ADR 0013)");
    }

    [Fact]
    public async Task A_tolerance_the_procedures_would_round_fails_without_retry_and_records_nothing()
    {
        var (host, _, date, runId) = await StartAsync("recon-card-off-by-one-cent");
        await using var _ = host;

        var run = await host.RunAsync(PipelineNames.Reconciliation, date, runId, tolerance: 0.005m);

        run.Succeeded.ShouldBeFalse();
        run.Result.Failure!.InnerFailure!.ErrorType.ShouldBe(typeof(ReconInputException).FullName);
        host.Trace.ShouldNotHaveRetried();
        (await ExceptionsAsync(date)).ShouldBeEmpty();
        run.Alerts.ShouldBeEmpty();
    }

    private async Task<(BatchHost Host, Scenario Scenario, DateOnly Date, Guid RunId)> StartAsync(string name)
    {
        var scenario = Scenario.Load(name);
        await Env.LoadAsync(scenario, Ct);
        var host = BatchHost.Start(Env, scenario);
        var date = scenario.BusinessDate();
        var runId = Guid.NewGuid();
        await Upstream.RunAsync(host, date, runId, PipelineNames.Extract, PipelineNames.CardSettlement, PipelineNames.MapAndAggregate);
        return (host, scenario, date, runId);
    }

    private async Task NudgeDailyGlTotalAsync(DateOnly date, decimal amount)
    {
        await using var connection = await ReportingDatabase.OpenAsync(Env.Reporting.AdminConnectionString, Ct);
        await using var command = new SqlCommand(
            "UPDATE core.DailyGlTotals SET DebitTotal = DebitTotal + @a, NetAmount = NetAmount + @a WHERE BusinessDate = @d AND GlAccountNbr = (SELECT MIN(GlAccountNbr) FROM core.DailyGlTotals WHERE BusinessDate = @d)", connection);
        command.Parameters.Add(new SqlParameter("@a", System.Data.SqlDbType.Decimal) { Precision = 19, Scale = 2, Value = amount });
        command.Parameters.AddWithValue("@d", date.ToDateTime(TimeOnly.MinValue));
        (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
    }

    private async Task<IReadOnlyList<ExceptionRow>> ExceptionsAsync(DateOnly date)
    {
        await using var connection = await ReportingDatabase.OpenAsync(Env.Reporting.AdminConnectionString, Ct);
        await using var command = new SqlCommand(
            "SELECT CheckName, ItemKey, Difference, DetectedBatchRunId, AlertBatchRunId, AlertSentUtc FROM ops.ReconExceptions WHERE BusinessDate = @d ORDER BY ReconExceptionId", connection);
        command.Parameters.AddWithValue("@d", date.ToDateTime(TimeOnly.MinValue));
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<ExceptionRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new ExceptionRow(reader.GetString(0), reader.GetString(1), reader.GetDecimal(2), reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetDateTime(5)));
        }

        return rows;
    }

    private async Task<List<string>> StatusesAsync(Guid runId)
    {
        await using var connection = await ReportingDatabase.OpenAsync(Env.Reporting.AdminConnectionString, Ct);
        await using var command = new SqlCommand("SELECT Status FROM ops.BatchRun WHERE BatchRunId = @r AND PipelineName = @p ORDER BY BatchRunEventId", connection);
        command.Parameters.AddWithValue("@r", runId);
        command.Parameters.AddWithValue("@p", PipelineNames.Reconciliation);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private async Task<string?> NotesAsync(Guid runId)
    {
        await using var connection = await ReportingDatabase.OpenAsync(Env.Reporting.AdminConnectionString, Ct);
        await using var command = new SqlCommand("SELECT TOP (1) Notes FROM ops.BatchRun WHERE BatchRunId = @r AND PipelineName = @p AND Notes IS NOT NULL ORDER BY BatchRunEventId DESC", connection);
        command.Parameters.AddWithValue("@r", runId);
        command.Parameters.AddWithValue("@p", PipelineNames.Reconciliation);
        return (string?)await command.ExecuteScalarAsync(Ct);
    }

    private IEnumerable<SensitiveValue> Secrets(BatchHost host)
    {
        var s = Env.Secrets;
        yield return new("the SQL Server sa password", s.SqlAdminPassword);
        yield return new("the cu_batch_writer password", s.SqlWriterPassword);
        yield return new("the KSEXTRACT password", s.Db2ExtractPassword);
        yield return new("the KeyBridge password", s.KeyBridgePassword);
        yield return new("the KeyBridge Basic credential", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{KeyBridgeStub.Username}:{s.KeyBridgePassword}")));
        yield return new("the alert webhook token", s.AlertWebhookToken);
        yield return new("the alert webhook URL", host.Webhook.Url);
        yield return new("the alert webhook path", "/hooks/recon-alerts/");
    }

    /// <summary>Every member, account and card number of the scenario (the AT-17 rule), labelled by its last four digits only.</summary>
    private static Dictionary<string, string> FullNumbers(Scenario scenario)
    {
        var numbers = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(TableData table, string column, string kind)
        {
            foreach (var value in table.Rows.Select(r => r[table.ColumnIndex(column)]).OfType<string>().Select(v => v.Trim()))
            {
                numbers.TryAdd(value, $"a full {kind} number (ending {value[^4..]})");
            }
        }

        Add(scenario.InputTable(KeyStoneTables.Member), "MEMBER_NBR", "member");
        Add(scenario.InputTable(KeyStoneTables.Account), "ACCOUNT_NBR", "account");
        if (scenario.Golden.Tables.Any(t => t.Name == GoldenTables.CardSettlement.Name))
        {
            var cards = scenario.GoldenTable(GoldenTables.CardSettlement.Name);
            foreach (var row in cards.Rows)
            {
                var card = row[cards.ColumnIndex("CardBin")] + row[cards.ColumnIndex("CardLast4")];
                numbers.TryAdd(card, $"a full card number (ending {card[^4..]})");
            }
        }

        numbers.ShouldNotBeEmpty();
        return numbers;
    }

    private sealed record ExceptionRow(string CheckName, string ItemKey, decimal Difference, Guid DetectedRun, Guid? AlertRun, DateTime? AlertSentUtc);
}
