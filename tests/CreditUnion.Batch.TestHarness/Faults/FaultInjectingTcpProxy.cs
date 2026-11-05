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
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
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
        listener.Start();
        acceptLoop = AcceptLoopAsync();
    }

    public static string Host => "127.0.0.1";

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public int RefusedConnections => Volatile.Read(ref refused);

    public int ForwardedConnections => Volatile.Read(ref forwarded);

    public static FaultInjectingTcpProxy Start(string targetHost, int targetPort) => new(targetHost, targetPort);

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
