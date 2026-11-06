using CreditUnion.Batch.TestHarness.Scenarios;

namespace CreditUnion.Batch.AcceptanceTests.Support;

/// <summary>
/// Preconditions for testing one child pipeline: the pipelines the master runs before it, with the
/// same business date and batch run ID (ADR 0006: a pipeline test may set up its inputs by running
/// the upstream pipelines; the build phases are ordered so those exist by then).
/// </summary>
public static class Upstream
{
    public static async Task RunAsync(BatchHost host, DateOnly businessDate, Guid batchRunId, params string[] pipelines)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(pipelines);
        foreach (var pipeline in pipelines)
        {
            (await host.RunAsync(pipeline, businessDate, batchRunId)).ShouldHaveSucceeded($"precondition {pipeline}");
        }

        host.Trace.Clear();
    }
}

public static class ScenarioExtensions
{
    /// <summary>The business date of the scenario's first run.</summary>
    public static DateOnly BusinessDate(this Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        return scenario.Runs[0].Date();
    }
}
