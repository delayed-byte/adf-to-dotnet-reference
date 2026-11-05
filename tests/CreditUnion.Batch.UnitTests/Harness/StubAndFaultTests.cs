using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using CreditUnion.Batch.TestData.KeyBridge;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Faults;
using CreditUnion.Batch.TestHarness.Golden;
using CreditUnion.Batch.TestHarness.Scenarios;
using CreditUnion.Batch.TestHarness.Stubs;

namespace CreditUnion.Batch.UnitTests.Harness;

/// <summary>Harness self-tests that need no containers: HTTP stubs, the TCP fault proxy and golden JSON comparison.</summary>
public sealed class StubAndFaultTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task KeyBridge_stub_serves_the_contract_mapping_only_with_the_right_credentials()
    {
        var password = TestSecrets.New();
        using var stub = new KeyBridgeStub(Scenario.Load("standard-day").KeyBridgeMappingJson, password);
        using var http = new HttpClient();

        using var anonymous = await http.GetAsync(new Uri(stub.BaseUrl + "/v1/reference/transaction-codes/gl-mappings"), Ct);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var request = MappingRequest(stub, password);
        using var response = await http.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var mapping = GlMappingResponse.Parse(await response.Content.ReadAsStringAsync(Ct));
        mapping.Version.ShouldBe("2026-11-01");
        mapping.Mappings.Count.ShouldBe(21);
        stub.Requests[^1].Headers["X-Correlation-Id"].ShouldBe("run-123");
    }

    [Fact]
    public async Task KeyBridge_stub_returns_503_twice_then_200_for_AT_11()
    {
        var password = TestSecrets.New();
        using var stub = new KeyBridgeStub("{\"version\":\"v\",\"mappings\":[]}", password);
        using var http = new HttpClient();
        stub.FailNextRequests(2);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var request = MappingRequest(stub, password);
            using var response = await http.SendAsync(request, Ct);
            statuses.Add(response.StatusCode);
        }

        statuses.ShouldBe([HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK, HttpStatusCode.OK]);
        stub.MappingRequestCount.ShouldBe(4);
    }

    [Fact]
    public async Task Alert_webhook_stub_accepts_posts_only_on_its_secret_url_and_records_them()
    {
        var token = TestSecrets.New();
        using var stub = new AlertWebhookStub(token);
        using var http = new HttpClient();

        using var wrong = await http.PostAsync(new Uri(stub.RootUrl + "/hooks/recon-alerts/other"), Json("{}"), Ct);
        using var right = await http.PostAsync(new Uri(stub.Url), Json("{\"type\":\"ReconciliationException\"}"), Ct);
        stub.FailNextRequests(1, 500);
        using var failed = await http.PostAsync(new Uri(stub.Url), Json("{\"retry\":1}"), Ct);

        wrong.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        right.StatusCode.ShouldBe(HttpStatusCode.OK);
        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        stub.Url.ShouldContain(token);
        stub.Alerts.ShouldBe(["{\"type\":\"ReconciliationException\"}", "{\"retry\":1}"]);
    }

    [Fact]
    public async Task Tcp_proxy_refuses_the_next_connections_then_forwards_traffic_for_AT_04()
    {
        using var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        var echoLoop = Task.Run(async () =>
        {
            using var socket = await echo.AcceptSocketAsync(Ct);
            var buffer = new byte[64];
            var read = await socket.ReceiveAsync(buffer, Ct);
            await socket.SendAsync(buffer.AsMemory(0, read), Ct);
        }, Ct);

        await using var proxy = FaultInjectingTcpProxy.Start("127.0.0.1", ((IPEndPoint)echo.LocalEndpoint).Port);
        proxy.RefuseNextConnections(2);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var refused = new TcpClient();
            await refused.ConnectAsync(FaultInjectingTcpProxy.Host, proxy.Port, Ct);
            var stream = refused.GetStream();
            var read = await ReadOrFailAsync(stream);
            read.ShouldBe(0, "a refused connection is reset before any data flows");
        }

        using var client = new TcpClient();
        await client.ConnectAsync(FaultInjectingTcpProxy.Host, proxy.Port, Ct);
        var payload = Encoding.ASCII.GetBytes("DRDA?");
        await client.GetStream().WriteAsync(payload, Ct);
        var reply = new byte[payload.Length];
        await client.GetStream().ReadExactlyAsync(reply, Ct);
        await echoLoop;

        reply.ShouldBe(payload);
        proxy.RefusedConnections.ShouldBe(2);
        proxy.ForwardedConnections.ShouldBe(1);
    }

    [Fact]
    public void Json_equivalence_compares_numbers_as_decimals_and_treats_missing_as_null()
    {
        JsonEquivalence.Differences("{\"a\":0.01,\"b\":null,\"c\":[1,2]}", "{\"c\":[1.0,2.00],\"a\":0.010}").ShouldBeEmpty();
        JsonEquivalence.Differences("{\"a\":0.01}", "{\"a\":0.02,\"x\":\"y\"}").ShouldBe(["$.a: expected 0.01, actual 0.02", "$.x: unexpected in actual (\"y\")"]);
    }

    [Fact]
    public void Run_tokens_map_run_ids_both_ways_and_evidence_timestamps_are_normalised()
    {
        var run1 = Guid.Parse("6f0c4a1e-2b8d-4d1c-9a55-0f3b2c1d4e5f");
        var tokens = new RunTokens([run1]);

        tokens.Resolve("evidence/2026-11-04/batch-run-{run:1}.json").ShouldBe("evidence/2026-11-04/batch-run-6f0c4a1e-2b8d-4d1c-9a55-0f3b2c1d4e5f.json");
        tokens.Tokenize("ID 6F0C4A1E-2B8D-4D1C-9A55-0F3B2C1D4E5F").ShouldBe("ID {run:1}");

        const string expected = "[{\"section\":\"Exceptions\",\"item\":\"CardSettlement/ALL\",\"value\":\"difference=0.01; tolerance=0.00; detectedUtc=<utc>; alertSentUtc=<utc>\"}]";
        const string actual = "[{\"section\":\"Run\",\"item\":\"generatedUtc\",\"value\":\"2026-11-05T11:00:01\"},{\"section\":\"Exceptions\",\"item\":\"CardSettlement/ALL\",\"value\":\"difference=0.01; tolerance=0.00; detectedUtc=2026-11-05T11:00:00.123; alertSentUtc=2026-11-05T11:00:00.456\"}]";
        GoldenAssert.EvidenceDifferences(expected, actual, tokens).ShouldBeEmpty();
        GoldenAssert.EvidenceDifferences(expected, actual.Replace("0.01", "0.02", StringComparison.Ordinal), tokens).Count.ShouldBe(2);
    }

    private static HttpRequestMessage MappingRequest(KeyBridgeStub stub, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(stub.BaseUrl + "/v1/reference/transaction-codes/gl-mappings"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{KeyBridgeStub.Username}:{password}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-Correlation-Id", "run-123");
        return request;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<int> ReadOrFailAsync(NetworkStream stream)
    {
        try
        {
            return await stream.ReadAsync(new byte[16], Ct);
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
