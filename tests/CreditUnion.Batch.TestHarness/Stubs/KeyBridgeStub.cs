using System.Text;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace CreditUnion.Batch.TestHarness.Stubs;

/// <summary>
/// KeyBridge stand-in implementing the placeholder contract (contracts/keybridge/README.md):
/// <c>GET {BaseUrl}/v1/reference/transaction-codes/gl-mappings</c> with Basic credentials returns the
/// scenario's mapping; wrong or missing credentials get 401. <see cref="BaseUrl"/> plays the role of the
/// pipeline parameter <c>keyBridgeBaseUrl</c>.
/// </summary>
public sealed class KeyBridgeStub : HttpStub
{
    public const string Username = "cu-batch";
    public const string MappingsPath = "/api/v1/reference/transaction-codes/gl-mappings";

    public KeyBridgeStub(string mappingJson, string password)
    {
        ArgumentNullException.ThrowIfNull(mappingJson);
        ArgumentNullException.ThrowIfNull(password);
        var credentials = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{password}"));
        Server.Given(Request.Create().WithPath(MappingsPath).UsingGet().WithHeader("Authorization", credentials))
            .AtPriority(10)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(mappingJson));
        Server.Given(Request.Create().WithPath(MappingsPath).UsingAnyMethod())
            .AtPriority(100)
            .RespondWith(Response.Create().WithStatusCode(401).WithBody("{\"error\":\"unauthorized\"}"));
    }

    public string BaseUrl => RootUrl + "/api";

    public int MappingRequestCount => Requests.Count(r => r.Path == MappingsPath);

    protected override IRequestBuilder Endpoint() => Request.Create().WithPath(MappingsPath).UsingGet();
}
