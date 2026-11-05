using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace CreditUnion.Batch.TestHarness.Stubs;

/// <summary>
/// The reconciliation alert webhook (Teams or email in production). As with the real one, the URL
/// embeds a secret token (Key Vault secret <c>recon-alert-webhook-url</c>), so AT-17 can check that the
/// URL never appears in a log line. Only POSTs to the exact tokenised path are accepted.
/// </summary>
public sealed class AlertWebhookStub : HttpStub
{
    private readonly string path;

    public AlertWebhookStub(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        path = "/hooks/recon-alerts/" + token;
        Server.Given(Request.Create().WithPath(path).UsingPost())
            .AtPriority(10)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"accepted\":true}"));
        Server.Given(Request.Create().WithPath("/*").UsingAnyMethod())
            .AtPriority(100)
            .RespondWith(Response.Create().WithStatusCode(404));
    }

    /// <summary>The secret webhook URL handed to the batch through configuration.</summary>
    public string Url => RootUrl + path;

    /// <summary>Bodies of the alerts accepted so far, oldest first.</summary>
    public IReadOnlyList<string> Alerts => [.. Requests.Where(r => r.Method == "POST" && r.Path == path).Select(r => r.Body)];

    protected override IRequestBuilder Endpoint() => Request.Create().WithPath(path).UsingPost();
}
