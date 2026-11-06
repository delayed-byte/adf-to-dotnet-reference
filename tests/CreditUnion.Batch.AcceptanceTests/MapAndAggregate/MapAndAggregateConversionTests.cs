using System.Globalization;
using System.Text;
using CreditUnion.Batch.AcceptanceTests.Harness;
using CreditUnion.Batch.AcceptanceTests.Support;
using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.Core.Map;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.KeyStone.Map;
using CreditUnion.Batch.TestData.Canonical;
using CreditUnion.Batch.TestData.Golden;
using CreditUnion.Batch.TestData.KeyStone;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Logging;
using CreditUnion.Batch.TestHarness.Scenarios;
using CreditUnion.Batch.TestHarness.Sql;
using CreditUnion.Batch.TestHarness.Stubs;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace CreditUnion.Batch.AcceptanceTests.MapAndAggregate;

/// <summary>
/// Conversion checks for PL_MapAndAggregate beyond AT-08 to AT-11 (ADR 0011, ADR 0012), against the real containers:
/// failures that are not retried, the sink's replace-in-one-transaction semantics, and the AT-17 rules applied to the
/// Map runs (the AT-17 cases themselves need the Phase 8 master).
/// </summary>
public sealed class MapAndAggregateConversionTests(BatchEnvironmentFixture fixture)
{
    private BatchEnvironment Env => fixture.Environment;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Scenarios => ["standard-day", "map-unmapped-code", "map-midpoint-rounding"];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Map_runs_log_no_secret_and_no_full_member_account_or_card_number(string name)
    {
        var scenario = Scenario.Load(name);
        await Env.LoadAsync(scenario, Ct);
        await using var host = BatchHost.Start(Env, scenario);
        var date = scenario.BusinessDate();
        var runId = Guid.NewGuid();
        await Upstream.RunAsync(host, date, runId, PipelineNames.Extract, PipelineNames.CardSettlement);
        host.KeyBridge.FailNextRequests(1, 503);

        var run = await host.RunAsync(PipelineNames.MapAndAggregate, date, runId);
        await host.RunAsync(PipelineNames.MapAndAggregate, date, Guid.NewGuid());

        run.KeyBridgeRequests.Count.ShouldBe(2, "one 503, then the retry");
        host.Logs.Logs.ShouldContain(l => l.Category.Contains("KeyBridge", StringComparison.Ordinal), "the KeyBridge client logs; an empty log would pass vacuously");
        var findings = host.Logs.Findings(Secrets(host), FullNumbers(scenario));
        findings.ShouldBeEmpty($"{findings.Count} log line(s) or span(s) expose sensitive values");
    }

    [Fact]
    public async Task A_401_from_KeyBridge_is_not_retried_and_touches_nothing_but_the_Started_event()
    {
        var scenario = Scenario.Load("standard-day");
        await Env.LoadAsync(scenario, Ct);
        await using var host = BatchHost.Start(Env, scenario);
        var runId = Guid.NewGuid();
        host.KeyBridge.FailNextRequests(1, 401);

        var run = await host.RunAsync(PipelineNames.MapAndAggregate, scenario.BusinessDate(), runId);

        run.Succeeded.ShouldBeFalse();
        run.Result.Failure!.InnerFailure!.ErrorType.ShouldBe(typeof(KeyBridgeRejectedException).FullName);
        run.KeyBridgeRequests.Count.ShouldBe(1, "a rejected request is not retried (ADR 0012)");
        host.Trace.ShouldNotHaveRetried();
        (await Env.Reporting.SnapshotAsync(GoldenTables.TxnCodeGlMap, [runId], Ct)).RowCount.ShouldBe(0);
        var events = await Env.Reporting.SnapshotAsync(GoldenTables.BatchRun, [runId], Ct);
        events.Rows.Select(r => r[events.ColumnIndex("Status")]).ShouldBe(["Started"], "the Web activity has no failure branch, so no Failed event");
        host.Logs.Findings(Secrets(host)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_mapping_the_procedure_rejects_is_not_retried_and_leaves_the_cached_mapping_unchanged()
    {
        var scenario = Scenario.Load("standard-day");
        await Env.LoadAsync(scenario, Ct);
        await using var host = BatchHost.Start(Env, scenario);
        var date = scenario.BusinessDate();
        Guid[] runIds = [Guid.NewGuid(), Guid.NewGuid()];
        (await host.RunAsync(PipelineNames.MapAndAggregate, date, runIds[0])).ShouldHaveSucceeded("no transactions are loaded, so nothing is unmapped");
        var cached = await Env.Reporting.SnapshotAsync(GoldenTables.TxnCodeGlMap, runIds, Ct);
        cached.RowCount.ShouldBeGreaterThan(0);
        host.Trace.Clear();
        host.KeyBridge.Server.Given(Request.Create().WithPath(KeyBridgeStub.MappingsPath).UsingGet()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("{\"version\":\"broken\",\"mappings\":[{\"transactionCode\":\"CPOS\",\"glAccountNumber\":\"2100\",\"side\":\"DEBIT\",\"allocationRatio\":0.5}]}"));

        var run = await host.RunAsync(PipelineNames.MapAndAggregate, date, runIds[1]);

        run.Succeeded.ShouldBeFalse();
        run.Result.Failure!.InnerFailure!.ErrorType.ShouldBe(typeof(GlMappingInvalidException).FullName);
        run.Result.Failure.InnerFailure.ErrorMessage.ShouldContain("KEYBRIDGE_MAPPING_INVALID");
        host.Trace.ShouldNotHaveRetried();
        var after = await Env.Reporting.SnapshotAsync(GoldenTables.TxnCodeGlMap, runIds, Ct);
        TableChecksum.Compute(after).ShouldBe(TableChecksum.Compute(cached), "the procedure validates the whole set before touching the cache");
    }

    [Fact]
    public async Task The_sink_replaces_only_the_business_date_in_one_transaction()
    {
        await Env.Reporting.ResetAsync(Ct);
        var store = new SqlMapAndAggregateStore(Options.Create(new ReportingSqlOptions { ConnectionString = Env.Reporting.WriterConnectionString }));
        var day = new DateOnly(2026, 11, 4);
        var before = day.AddDays(-1);
        DailyGlTotal Row(DateOnly d, string account, decimal debit, decimal credit, int count) => new(d, account.PadRight(10), debit, credit, debit - credit, count);

        await store.ReplaceDailyGlTotalsAsync(before, [Row(before, "1010", 1.00m, 0.00m, 1)], Ct);
        await store.ReplaceDailyGlTotalsAsync(day, [Row(day, "1010", 5.00m, 0.00m, 2), Row(day, "2100", 0.00m, 5.00m, 2)], Ct);
        await store.ReplaceDailyGlTotalsAsync(day, [Row(day, "2100", 0.00m, 7.25m, 3)], Ct);
        (await ReadAsync(day)).ShouldBe(["2100      |0.00|7.25|-7.25|3"], "a rerun replaces the date's rows");
        (await ReadAsync(before)).ShouldBe(["1010      |1.00|0.00|1.00|1"], "other dates are untouched");

        await Should.ThrowAsync<ArgumentException>(() => store.ReplaceDailyGlTotalsAsync(day, [Row(day, "1010", 1.00m, 0.00m, 1), Row(day, "2100", 0.005m, 0.00m, 1)], Ct));
        (await ReadAsync(day)).ShouldBe(["2100      |0.00|7.25|-7.25|3"], "a failed write rolls back the delete too");

        var many = Enumerable.Range(1, 700).Select(i => Row(day, "G" + i.ToString("D9", CultureInfo.InvariantCulture), i, 0.00m, 1)).ToList();
        await store.ReplaceDailyGlTotalsAsync(day, many, Ct);
        (await ReadAsync(day)).Count.ShouldBe(700, "rows are inserted in chunks under the parameter limit");

        await store.ReplaceDailyGlTotalsAsync(day, [], Ct);
        (await ReadAsync(day)).ShouldBeEmpty("an empty result still runs the pre-SQL delete");
        (await ReadAsync(before)).Count.ShouldBe(1);
    }

    private async Task<List<string>> ReadAsync(DateOnly date)
    {
        await using var connection = await ReportingDatabase.OpenAsync(Env.Reporting.AdminConnectionString, Ct);
        await using var command = new SqlCommand(
            "SELECT GlAccountNbr, DebitTotal, CreditTotal, NetAmount, TxnCount FROM core.DailyGlTotals WHERE BusinessDate = @d ORDER BY GlAccountNbr", connection);
        command.Parameters.AddWithValue("@d", date.ToDateTime(TimeOnly.MinValue));
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(string.Join('|', reader.GetString(0), CanonicalValue.Decimal(reader.GetDecimal(1), 2), CanonicalValue.Decimal(reader.GetDecimal(2), 2),
                CanonicalValue.Decimal(reader.GetDecimal(3), 2), reader.GetInt32(4).ToString(CultureInfo.InvariantCulture)));
        }

        return rows;
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
}
