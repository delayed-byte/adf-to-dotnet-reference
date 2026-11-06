using System.Globalization;
using CreditUnion.Batch.AcceptanceTests.Harness;
using CreditUnion.Batch.AcceptanceTests.Support;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.TestData.Golden;
using CreditUnion.Batch.TestData.KeyStone;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Adf;
using CreditUnion.Batch.TestHarness.Golden;
using CreditUnion.Batch.TestHarness.Scenarios;

namespace CreditUnion.Batch.AcceptanceTests.MapAndAggregate;

/// <summary>
/// PL_MapAndAggregate (adf/pipeline/PL_MapAndAggregate.json): AT-08 to AT-11. Each test first runs
/// Extract and Card Settlement for the same business date and batch run ID, as the master does.
/// </summary>
public sealed class MapAndAggregateTests(BatchEnvironmentFixture fixture)
{
    private static readonly string[] Owned = [PipelineNames.MapAndAggregate];

    private BatchEnvironment Env => fixture.Environment;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("AT", "AT-08")]
    public async Task AT08_when_every_code_is_mapped_DailyGlTotals_match_golden_to_the_cent()
    {
        var scenario = Scenario.Load("standard-day");
        var (host, date, runIds) = await StartAsync(scenario);
        await using var _ = host;

        var run = await host.RunAsync(PipelineNames.MapAndAggregate, date, runIds[0]);

        run.ShouldHaveSucceeded();
        await GoldenAssert.TablesMatchAsync(Env.Reporting, scenario, runIds, Owned, Ct);
        var request = run.KeyBridgeRequests.ShouldHaveSingleItem("one KeyBridge call for the GL mapping");
        request.Headers.ShouldContainKey("X-Correlation-Id");
        Guid.Parse(request.Headers["X-Correlation-Id"]).ShouldBe(runIds[0], "the call is correlated by batchRunId, as the ADF Web activity sends it");
        await Idempotency.RerunShouldChangeNothingAsync(host, runIds, [run], async () => [await host.RunAsync(PipelineNames.MapAndAggregate, date, Guid.NewGuid())]);
    }

    [Fact]
    [Trait("AT", "AT-09")]
    public async Task AT09_an_unmapped_transaction_code_fails_with_TXN_CODE_UNMAPPED_naming_the_code()
    {
        var scenario = Scenario.Load("map-unmapped-code");
        var (host, date, runIds) = await StartAsync(scenario);
        await using var _ = host;

        var run = await host.RunAsync(PipelineNames.MapAndAggregate, date, runIds[0]);

        run.ShouldHaveFailedWith(PipelineErrorCodes.TxnCodeUnmapped);
        run.Result.Failure!.ErrorMessage.ShouldContain("Unmapped transaction codes: P2PD");
        var messages = scenario.ExpectedRun(1).Messages.Where(m => m.Pipeline == PipelineNames.MapAndAggregate).ToList();
        messages.ShouldNotBeEmpty();
        await Env.Reporting.ShouldHaveLoggedMessagesAsync(runIds[0], messages, Ct);
        await GoldenAssert.TablesMatchAsync(Env.Reporting, scenario, runIds, Owned, Ct);
        host.Trace.ShouldNotHaveRetried();
        await Idempotency.RerunShouldChangeNothingAsync(host, runIds, [run], async () => [await host.RunAsync(PipelineNames.MapAndAggregate, date, Guid.NewGuid())]);
    }

    [Fact]
    [Trait("AT", "AT-10")]
    public async Task AT10_derived_amounts_at_a_midpoint_use_bankers_rounding()
    {
        var scenario = Scenario.Load("map-midpoint-rounding");
        var (host, date, runIds) = await StartAsync(scenario);
        await using var _ = host;

        var run = await host.RunAsync(PipelineNames.MapAndAggregate, date, runIds[0]);

        run.ShouldHaveSucceeded();
        await GoldenAssert.TablesMatchAsync(Env.Reporting, scenario, runIds, Owned, Ct);

        // The rule restated from the inputs: each LPMT payment is split 75% to GL 1300 and 25% to GL 4100,
        // and each allocation is rounded to the cent with MidpointRounding.ToEven before summing (ADR 0007).
        var payments = scenario.InputTable(KeyStoneTables.PostedTxn).Where("TXN_CODE", "LPMT");
        var amounts = payments.Rows.Select(r => decimal.Parse(r[payments.ColumnIndex("AMOUNT")]!, CultureInfo.InvariantCulture)).ToList();
        amounts.Count.ShouldBe(12);
        decimal Total(decimal ratio, MidpointRounding mode) => amounts.Sum(a => decimal.Round(a * ratio, 2, mode));
        var totals = await Env.Reporting.SnapshotAsync(GoldenTables.DailyGlTotals, runIds, Ct);
        decimal Credit(string glAccount) => decimal.Parse(
            totals.Rows.Single(r => r[totals.ColumnIndex("GlAccountNbr")]!.TrimEnd() == glAccount)[totals.ColumnIndex("CreditTotal")]!, CultureInfo.InvariantCulture);
        Credit("1300").ShouldBe(Total(0.75m, MidpointRounding.ToEven));
        Credit("4100").ShouldBe(Total(0.25m, MidpointRounding.ToEven));
        Total(0.75m, MidpointRounding.AwayFromZero).ShouldNotBe(Total(0.75m, MidpointRounding.ToEven), "the scenario tells the two roundings apart");
        Total(0.25m, MidpointRounding.AwayFromZero).ShouldNotBe(Total(0.25m, MidpointRounding.ToEven));
        await Idempotency.RerunShouldChangeNothingAsync(host, runIds, [run], async () => [await host.RunAsync(PipelineNames.MapAndAggregate, date, Guid.NewGuid())]);
    }

    [Fact]
    [Trait("AT", "AT-11")]
    public async Task AT11_KeyBridge_503_twice_then_200_is_retried_per_the_ADF_web_policy_and_succeeds()
    {
        var scenario = Scenario.Load("standard-day");
        var (host, date, runIds) = await StartAsync(scenario);
        await using var _ = host;
        var webPolicy = AdfActivityPolicy.Read(PipelineNames.MapAndAggregate, "Get GL Mapping From KeyBridge");
        host.KeyBridge.FailNextRequests(2, 503);

        var run = await host.RunAsync(PipelineNames.MapAndAggregate, date, runIds[0]);

        run.ShouldHaveSucceeded();
        run.KeyBridgeRequests.Count.ShouldBe(3, "two 503 responses, then 200: no extra calls");
        host.Trace.ShouldHaveRetriedPerPolicy(webPolicy, minFailures: 2, maxFailures: 2);
        host.Trace.FailedAttempts.Select(a => a.CallId).Distinct().ShouldHaveSingleItem("both failures are attempts of the same KeyBridge call");
        await GoldenAssert.TablesMatchAsync(Env.Reporting, scenario, runIds, Owned, Ct);
        await Idempotency.RerunShouldChangeNothingAsync(host, runIds, [run], async () => [await host.RunAsync(PipelineNames.MapAndAggregate, date, Guid.NewGuid())]);
    }

    private async Task<(BatchHost Host, DateOnly Date, Guid[] RunIds)> StartAsync(Scenario scenario)
    {
        await Env.LoadAsync(scenario, Ct);
        var host = BatchHost.Start(Env, scenario);
        var date = scenario.BusinessDate();
        Guid[] runIds = [Guid.NewGuid()];
        await Upstream.RunAsync(host, date, runIds[0], PipelineNames.Extract, PipelineNames.CardSettlement);
        return (host, date, runIds);
    }
}
