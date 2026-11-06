using System.Net;
using System.Net.Sockets;

namespace CreditUnion.Batch.TestHarness.Faults;

/// <summary>
/// A local TCP forwarder placed between the batch and a container (AT-04: "Db2 connection refused for
/// the first two attempts"). Connections pass through unchanged, except that the next N connections
/// can be refused: they are accepted and immediately reset (RST), which the Db2 driver reports as a
/// communication failure (SQL30081N) just like a refused connection.
/// </summary>
public sealed class FaultInjectingTcpProxy : IAsyncDisposable
{
    /// <summary>
    /// First port of the block proxies listen on. The Db2 CLI driver keys its per-database settings by exact
    /// host and port and reads them once per process, so a proxy that must see the driver's own connection
    /// retry switched off (ADR 0009) has to sit on a port that is known when the process starts.
    /// <see cref="BatchEnvironment"/> registers the block before the first Db2 connection.
    /// </summary>
    public const int ReservedPortStart = 15600;

    /// <summary>Number of ports in the block that starts at <see cref="ReservedPortStart"/>.</summary>
    public const int ReservedPortCount = 16;

    private readonly TcpListener listener = ListenOnReservedPort();
    private readonly CancellationTokenSource stopping = new();
    private readonly string targetHost;
    private readonly int targetPort;
    private readonly Task acceptLoop;
    private int toRefuse;
    private int refused;
    private int forwarded;

    private FaultInjectingTcpProxy(string targetHost, int targetPort)
    {
        this.targetHost = targetHost;
        this.targetPort = targetPort;
        acceptLoop = AcceptLoopAsync();
    }

    public static string Host => "127.0.0.1";

    /// <summary>The ports a proxy tries first, in order; when all are busy it falls back to an ephemeral port.</summary>
    public static IReadOnlyList<int> ReservedPorts { get; } = [.. Enumerable.Range(ReservedPortStart, ReservedPortCount)];

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public int RefusedConnections => Volatile.Read(ref refused);

    public int ForwardedConnections => Volatile.Read(ref forwarded);

    public static FaultInjectingTcpProxy Start(string targetHost, int targetPort) => new(targetHost, targetPort);

    private static TcpListener ListenOnReservedPort()
    {
        foreach (var port in ReservedPorts)
        {
            var candidate = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            try
            {
                candidate.Start();
                return candidate;
            }
            catch (SocketException)
            {
                candidate.Stop();
            }
        }

        var fallback = new TcpListener(IPAddress.Loopback, 0);
        fallback.Start();
        return fallback;
    }

    public void RefuseNextConnections(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Interlocked.Exchange(ref toRefuse, count);
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        listener.Stop();
        try
        {
            await acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        stopping.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptSocketAsync(stopping.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            if (TryTakeRefusal())
            {
                Interlocked.Increment(ref refused);
                client.LingerState = new LingerOption(true, 0);
                client.Close();
                continue;
            }

            Interlocked.Increment(ref forwarded);
            _ = ForwardAsync(client);
        }
    }

    private bool TryTakeRefusal()
    {
        while (true)
        {
            var current = Volatile.Read(ref toRefuse);
            if (current == 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref toRefuse, current - 1, current) == current)
            {
                return true;
            }
        }
    }

    private async Task ForwardAsync(Socket client)
    {
        using var clientStream = new NetworkStream(client, ownsSocket: true);
        using var server = new TcpClient();
        try
        {
            await server.ConnectAsync(targetHost, targetPort, stopping.Token).ConfigureAwait(false);
            await using var serverStream = server.GetStream();
            var up = clientStream.CopyToAsync(serverStream, stopping.Token);
            var down = serverStream.CopyToAsync(clientStream, stopping.Token);
            await Task.WhenAny(up, down).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Either side closed the connection; nothing to clean up beyond the using blocks.
        }
    }
}
