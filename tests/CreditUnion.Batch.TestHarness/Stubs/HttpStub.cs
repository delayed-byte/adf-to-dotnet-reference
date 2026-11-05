using System.Globalization;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace CreditUnion.Batch.TestHarness.Stubs;

/// <summary>A request the stub received.</summary>
public sealed record StubRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>
/// Base for the in-process WireMock.Net stubs (no network beyond localhost). Faults are injected
/// with a WireMock scenario: the next N matching requests get an error status, then the normal
/// response resumes, which is how AT-11 (KeyBridge 503 twice, then 200) is driven.
/// </summary>
public abstract class HttpStub : IDisposable
{
    private const int FaultPriority = 1;
    private readonly string faultScenario = "faults-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private int faultGeneration;

    protected HttpStub()
    {
        Server = WireMockServer.Start();
    }

    public WireMockServer Server { get; }

    public string RootUrl => Server.Url!;

    /// <summary>Requests received so far, oldest first.</summary>
    public IReadOnlyList<StubRequest> Requests => [.. Server.LogEntries.Select(e => e.RequestMessage).OfType<WireMock.IRequestMessage>().OrderBy(m => m.DateTime).Select(m => new StubRequest(
        m.Method,
        m.Path,
        m.Headers?.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string>(),
        m.Body ?? string.Empty))];

    /// <summary>The next <paramref name="count"/> requests matching the stub's endpoint fail with <paramref name="statusCode"/>.</summary>
    public void FailNextRequests(int count, int statusCode = 503)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var scenario = faultScenario + "-" + (++faultGeneration).ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < count; i++)
        {
            var mapping = Server.Given(Endpoint()).AtPriority(FaultPriority).InScenario(scenario);
            if (i > 0)
            {
                mapping = mapping.WhenStateIs($"failed-{i}");
            }

            mapping.WillSetStateTo($"failed-{i + 1}").RespondWith(Response.Create().WithStatusCode(statusCode).WithBody("{\"error\":\"injected fault\"}"));
        }
    }

    public void ClearRequests() => Server.ResetLogEntries();

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>The request matcher for the stub's main endpoint (used for faults).</summary>
    protected abstract IRequestBuilder Endpoint();

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Server.Stop();
            Server.Dispose();
        }
    }
}
