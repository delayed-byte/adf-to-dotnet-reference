using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Faults;
using IBM.Data.Db2;

namespace CreditUnion.Batch.AcceptanceTests.Harness;

/// <summary>
/// Pins what the Db2 CLI driver does when a connection fails (ADR 0009). The extract's retries are the
/// orchestrator's RetryPolicy (ADR 0008), so a driver that quietly retries underneath would hide failed
/// attempts from the audit trail and from AT-04. The proxy listens on a reserved port the harness
/// configured before the process's first Db2 connection; the driver reads its settings only once.
/// </summary>
public sealed class Db2ConnectionRetryProbeTests(BatchEnvironmentFixture fixture)
{
    private BatchEnvironment Env => fixture.Environment;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_refused_connection_is_one_attempt_the_driver_does_not_retry_it()
    {
        await using var proxy = FaultInjectingTcpProxy.Start(Env.KeyStone.Host, Env.KeyStone.Port);
        FaultInjectingTcpProxy.ReservedPorts.ShouldContain(proxy.Port, "the proxy must sit on a port whose driver settings were registered at start");
        proxy.RefuseNextConnections(2);
        var connectionString = Env.KeyStone.ExtractConnectionStringVia(FaultInjectingTcpProxy.Host, proxy.Port);
        var started = DateTime.UtcNow;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await using var connection = new DB2Connection(connectionString);
            await Should.ThrowAsync<DB2Exception>(() => connection.OpenAsync(Ct));
            proxy.RefusedConnections.ShouldBe(attempt, "each Open makes exactly one connection attempt");
            proxy.ForwardedConnections.ShouldBe(0);
        }

        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(4), "the driver's default retry waits 5 seconds before its last attempt");

        await using var third = new DB2Connection(connectionString);
        await third.OpenAsync(Ct);
        proxy.ForwardedConnections.ShouldBe(1);
    }
}
