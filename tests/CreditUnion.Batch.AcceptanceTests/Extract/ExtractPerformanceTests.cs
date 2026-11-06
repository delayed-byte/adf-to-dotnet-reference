using System.Diagnostics;
using System.Globalization;
using CreditUnion.Batch.AcceptanceTests.Harness;
using CreditUnion.Batch.AcceptanceTests.Support;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.TestData.Golden;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Golden;
using CreditUnion.Batch.TestHarness.Scenarios;

namespace CreditUnion.Batch.AcceptanceTests.Extract;

/// <summary>
/// The performance NFR (PRD): PL_ExtractKeyStone reaches at least 100,000 rows per minute on the
/// perf-extract-1m scenario (1,000,000 transactions, 50,000 accounts and 2,000 GL entries for one business date).
/// Excluded from the default run: <c>./build.ps1 test -Performance</c>. The measurement is the wall-clock time of
/// the whole pipeline (log, lookup, three copies, merge, log), against the rows it brought into core; loading
/// Db2 and comparing the result with the golden checksums are outside it. The result is also written to
/// <c>tests/TestData/generated/perf-extract-1m/throughput.txt</c> (git-ignored).
/// </summary>
public sealed class ExtractPerformanceTests(BatchEnvironmentFixture fixture)
{
    private const double MinimumRowsPerMinute = 100_000;

    private BatchEnvironment Env => fixture.Environment;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Performance")]
    [Trait("AT", "NFR-Performance")]
    public async Task Extracting_a_million_transactions_reaches_100000_rows_per_minute_and_matches_the_golden_checksums()
    {
        var scenario = Scenario.Load("perf-extract-1m");
        var loading = Stopwatch.StartNew();
        await Env.LoadAsync(scenario, Ct);
        loading.Stop();
        await using var host = BatchHost.Start(Env, scenario);
        Guid[] runIds = [Guid.NewGuid()];
        var rows = new[] { GoldenTables.PostedTransaction, GoldenTables.Account, GoldenTables.GlEntry }.Sum(t => scenario.GoldenTableExpectation(t.Name).RowCount);

        var extracting = Stopwatch.StartNew();
        var run = await host.RunAsync(PipelineNames.Extract, scenario.BusinessDate(), runIds[0]);
        extracting.Stop();

        var rowsPerMinute = rows / extracting.Elapsed.TotalMinutes;
        var report = string.Create(CultureInfo.InvariantCulture,
            $"perf-extract-1m: {rows:N0} rows extracted in {extracting.Elapsed.TotalSeconds:0.0} s = {rowsPerMinute:N0} rows per minute (minimum {MinimumRowsPerMinute:N0}); Db2 load took {loading.Elapsed.TotalSeconds:0.0} s");
        var file = Path.Combine(scenario.InputsDirectory, "..", "..", "perf-extract-1m", "throughput.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, report + Environment.NewLine, Ct);
        TestContext.Current.SendDiagnosticMessage(report);

        run.ShouldHaveSucceeded();
        await GoldenAssert.TablesMatchAsync(Env.Reporting, scenario, runIds, [PipelineNames.Extract], Ct);
        rowsPerMinute.ShouldBeGreaterThanOrEqualTo(MinimumRowsPerMinute, report);
    }
}
