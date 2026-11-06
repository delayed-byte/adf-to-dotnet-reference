using System.Text;
using CreditUnion.Batch.AcceptanceTests.Harness;
using CreditUnion.Batch.AcceptanceTests.Support;
using CreditUnion.Batch.TestData.Canonical;
using CreditUnion.Batch.TestData.Golden;
using CreditUnion.Batch.TestData.KeyStone;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Logging;
using CreditUnion.Batch.TestHarness.Scenarios;
using CreditUnion.Batch.TestHarness.Stubs;

namespace CreditUnion.Batch.AcceptanceTests.CrossCutting;

/// <summary>
/// AT-17: no secret and no full member, account or card number in any log line or trace span the
/// batch produces (PRD: mask to the last 4 digits). The nightly batch runs on three scenarios so every
/// path is covered: the happy path, a reconciliation alert (the secret webhook URL is used) and a
/// failure (exceptions and failure details are logged).
/// </summary>
public sealed class LogHygieneTests(BatchEnvironmentFixture fixture)
{
    private BatchEnvironment Env => fixture.Environment;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Scenarios => ["standard-day", "recon-card-off-by-one-cent", "map-unmapped-code"];

    [Theory]
    [Trait("AT", "AT-17")]
    [MemberData(nameof(Scenarios))]
    public async Task AT17_logs_and_traces_contain_no_secrets_and_no_full_member_account_or_card_numbers(string name)
    {
        var scenario = Scenario.Load(name);
        await Env.LoadAsync(scenario, Ct);
        await using var host = BatchHost.Start(Env, scenario);
        Guid[] runIds = [Guid.NewGuid()];

        var run = await host.RunAsync(scenario.Runs[0], runIds[0]);
        run.ShouldMatchOutcome(scenario.ExpectedRun(1));
        await Idempotency.RerunShouldChangeNothingAsync(host, runIds, [run], async () => [await host.RunAsync(scenario.Runs[0], Guid.NewGuid())]);

        host.Logs.Logs.ShouldNotBeEmpty("the batch logs its progress; an empty log would pass this test vacuously");
        var findings = host.Logs.Findings(Secrets(host), FullNumbers(scenario));
        findings.ShouldBeEmpty($"{findings.Count} log line(s) or span(s) expose sensitive values");
    }

    private IEnumerable<SensitiveValue> Secrets(BatchHost host)
    {
        var s = Env.Secrets;
        yield return new("the Db2 instance-owner password", s.Db2AdminPassword);
        yield return new("the KSEXTRACT password", s.Db2ExtractPassword);
        yield return new("the SQL Server sa password", s.SqlAdminPassword);
        yield return new("the cu_batch_writer password", s.SqlWriterPassword);
        yield return new("the cu_report_reader password", s.SqlReaderPassword);
        yield return new("the KeyBridge password", s.KeyBridgePassword);
        yield return new("the KeyBridge Basic credential", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{KeyBridgeStub.Username}:{s.KeyBridgePassword}")));
        yield return new("the alert webhook token", s.AlertWebhookToken);
        yield return new("the alert webhook URL", host.Webhook.Url);
    }

    /// <summary>Every member, account and card number in the scenario, labelled by its last four digits only.</summary>
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

        numbers.Count.ShouldBeGreaterThan(100, "the scenario provides member, account and card numbers to look for");
        return numbers;
    }
}
