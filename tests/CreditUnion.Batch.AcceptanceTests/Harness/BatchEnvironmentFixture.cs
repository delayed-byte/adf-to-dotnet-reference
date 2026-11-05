using CreditUnion.Batch.AcceptanceTests.Harness;
using CreditUnion.Batch.TestHarness;

// One set of containers per test run (Db2 takes one to three minutes to start); tests share it and
// reset it between scenarios, so they must not run in parallel (xunit.runner.json disables it).
[assembly: AssemblyFixture(typeof(BatchEnvironmentFixture))]

namespace CreditUnion.Batch.AcceptanceTests.Harness;

/// <summary>Starts the <see cref="BatchEnvironment"/> once for the assembly.</summary>
public sealed class BatchEnvironmentFixture : IAsyncLifetime
{
    private BatchEnvironment? environment;

    public BatchEnvironment Environment => environment ?? throw new InvalidOperationException("The batch environment has not started.");

    public async ValueTask InitializeAsync() => environment = await BatchEnvironment.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (environment is not null)
        {
            await environment.DisposeAsync();
        }
    }
}
