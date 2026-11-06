using CreditUnion.Batch.AcceptanceTests.Harness;
using CreditUnion.Batch.AcceptanceTests.Support;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.TestData.Golden;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Golden;
using CreditUnion.Batch.TestHarness.Scenarios;

namespace CreditUnion.Batch.AcceptanceTests.Reconciliation;

/// <summary>
/// PL_Reconciliation (adf/pipeline/PL_Reconciliation.json): AT-12 and AT-13. Each test first runs
/// Extract, Card Settlement and Map and Aggregate for the same business date and batch run ID.
/// </summary>
public sealed class ReconciliationTests(BatchEnvironmentFixture fixture)
{
    private static readonly string[] Owned = [PipelineNames.Reconciliation];

    private BatchEnvironment Env => fixture.Environment;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("AT", "AT-12")]
    public async Task AT12_when_card_and_GL_totals_match_the_run_is_Reconciled_and_no_alert_is_sent()
    {
        var scenario = Scenario.Load("standard-day");
        var (host, date, runIds) = await StartAsync(scenario);
        await using var _ = host;

        var run = await host.RunAsync(PipelineNames.Reconciliation, date, runIds[0], tolerance: 0.00m);

        run.ShouldHaveSucceeded();
        run.Alerts.ShouldBeEmpty("no alert when everything reconciles");
        scenario.GoldenTableExpectation(GoldenTables.ReconExceptions.Name).RowCount.ShouldBe(0);
        scenario.GoldenTable(GoldenTables.BatchRun.Name).Where("PipelineName", PipelineNames.Reconciliation).Where("Status", "Reconciled").RowCount.ShouldBe(1);
        await GoldenAssert.TablesMatchAsync(Env.Reporting, scenario, runIds, Owned, Ct);
        await Idempotency.RerunShouldChangeNothingAsync(host, runIds, [run], async () => [await host.RunAsync(PipelineNames.Reconciliation, date, Guid.NewGuid())]);
    }

    [Fact]
    [Trait("AT", "AT-13")]
    public async Task AT13_a_card_difference_of_one_cent_writes_one_exception_and_posts_exactly_one_alert()
    {
        var scenario = Scenario.Load("recon-card-off-by-one-cent");
        var (host, date, runIds) = await StartAsync(scenario);
        await using var _ = host;

        var run = await host.RunAsync(PipelineNames.Reconciliation, date, runIds[0], tolerance: 0.00m);

        run.ShouldHaveSucceeded("exceptions are recorded, not raised");
        var exceptions = scenario.GoldenTable(GoldenTables.ReconExceptions.Name);
        exceptions.RowCount.ShouldBe(1);
        (exceptions.Value(0, "CheckName"), exceptions.Value(0, "Difference")).ShouldBe(("CardSettlement", "0.01"));
        await GoldenAssert.TablesMatchAsync(Env.Reporting, scenario, runIds, Owned, Ct);
        run.Alerts.Count.ShouldBe(1, "exactly one alert");
        GoldenAssert.AlertDifferences(scenario, 1, run.Alerts, new RunTokens(runIds)).ShouldBeEmpty();
        await Idempotency.RerunShouldChangeNothingAsync(host, runIds, [run], async () => [await host.RunAsync(PipelineNames.Reconciliation, date, Guid.NewGuid())]);
    }

    private async Task<(BatchHost Host, DateOnly Date, Guid[] RunIds)> StartAsync(Scenario scenario)
    {
        await Env.LoadAsync(scenario, Ct);
        var host = BatchHost.Start(Env, scenario);
        var date = scenario.BusinessDate();
        Guid[] runIds = [Guid.NewGuid()];
        await Upstream.RunAsync(host, date, runIds[0], PipelineNames.Extract, PipelineNames.CardSettlement, PipelineNames.MapAndAggregate);
        return (host, date, runIds);
    }
}
