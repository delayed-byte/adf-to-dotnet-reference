using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.Core.Reconciliation;
using CreditUnion.Batch.Functions;
using CreditUnion.Batch.KeyStone.Reconciliation;
using CreditUnion.Batch.TestHarness.Logging;
using CreditUnion.Batch.TestHarness.Stubs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace CreditUnion.Batch.UnitTests.Reconciliation;

/// <summary>
/// The Web activity "Post Recon Alert" against the in-process webhook stub (ADR 0013): the request, the classification of every
/// failure for the RetryPolicy, and that the URL, whose path embeds a secret token, reaches no exception message, log line or
/// span. One call is always exactly one HTTP request.
/// </summary>
public sealed class ReconAlertClientTests : IDisposable
{
    private const string Body = "{\"type\":\"ReconciliationException\",\"businessDate\":\"2026-11-04\"}";

    private readonly string token = "tok-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly List<IDisposable> disposables = [];

    [Fact]
    public async Task It_sends_one_POST_with_the_body_and_exactly_the_ADF_content_type_and_nothing_else_that_identifies_the_caller()
    {
        var stub = Stub();

        await Client(stub).PostAsync(Body, TestContext.Current.CancellationToken);

        var request = stub.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe("POST");
        request.Path.ShouldBe("/hooks/recon-alerts/" + token);
        request.Body.ShouldBe(Body);
        request.Headers["Content-Type"].ShouldBe("application/json", "ADF sends the header as configured: no charset parameter");
        request.Headers.ShouldNotContainKey("Authorization");
        request.Headers.ShouldNotContainKey("Cookie");
    }

    [Theory]
    [InlineData(201)]
    [InlineData(202)]
    [InlineData(204)]
    public async Task Any_2xx_is_success(int status)
    {
        var stub = Stub();
        stub.FailNextRequests(1, status);

        await Client(stub).PostAsync(Body, TestContext.Current.CancellationToken);

        stub.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(503)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(504)]
    [InlineData(429)]
    [InlineData(408)]
    public async Task Transient_statuses_raise_ReconAlertUnavailable_which_the_policy_retries(int status)
    {
        var stub = Stub();
        stub.FailNextRequests(1, status);

        var e = await Should.ThrowAsync<ReconAlertUnavailableException>(() => Client(stub).PostAsync(Body, TestContext.Current.CancellationToken));

        e.Message.ShouldContain(status.ToString(System.Globalization.CultureInfo.InvariantCulture));
        stub.Requests.Count.ShouldBe(1, "the client never retries; the RetryPolicy does");
        ShouldNotLeak(e.Message, stub);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(302)]
    [InlineData(307)]
    public async Task Other_statuses_raise_ReconAlertRejected_which_is_not_retried(int status)
    {
        var stub = Stub();
        stub.FailNextRequests(1, status);

        var e = await Should.ThrowAsync<ReconAlertRejectedException>(() => Client(stub).PostAsync(Body, TestContext.Current.CancellationToken));

        stub.Requests.Count.ShouldBe(1, "a redirect is not followed, so the alert goes to the configured host only");
        ShouldNotLeak(e.Message, stub);
    }

    [Fact]
    public async Task A_rotated_token_gets_404_from_the_stub_which_is_rejected_without_naming_either_token()
    {
        var stub = Stub();
        var options = new ReconAlertWebhookOptions { Url = new Uri(stub.RootUrl + "/hooks/recon-alerts/old-" + token) };

        var e = await Should.ThrowAsync<ReconAlertRejectedException>(() => Client(stub, options).PostAsync(Body, TestContext.Current.CancellationToken));

        e.Message.ShouldContain("404");
        e.Message.ShouldNotContain("old-");
        ShouldNotLeak(e.Message, stub);
    }

    [Fact]
    public async Task No_answer_within_the_http_request_timeout_raises_TimeoutException_without_the_URL()
    {
        var stub = Stub();
        stub.Server.Given(Request.Create().WithPath("/hooks/recon-alerts/" + token).UsingPost()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(5)));

        var e = await Should.ThrowAsync<TimeoutException>(() => Client(stub, timeout: TimeSpan.FromMilliseconds(300)).PostAsync(Body, TestContext.Current.CancellationToken));

        e.Message.ShouldContain("httpRequestTimeout");
        ShouldNotLeak(e.Message, stub);
    }

    [Fact]
    public async Task A_refused_connection_raises_ReconAlertUnavailable_without_the_URL()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var options = new ReconAlertWebhookOptions { Url = new Uri($"http://127.0.0.1:{port}/hooks/recon-alerts/{token}") };

        var e = await Should.ThrowAsync<ReconAlertUnavailableException>(() => Client(null, options).PostAsync(Body, TestContext.Current.CancellationToken));

        e.Message.ShouldNotContain(token);
        e.Message.ShouldNotContain("/hooks");
        e.InnerException.ShouldBeNull("the driver's exception is not attached");
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_turned_into_a_timeout()
    {
        var stub = Stub();
        stub.Server.Given(Request.Create().WithPath("/hooks/recon-alerts/" + token).UsingPost()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(5)));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(() => Client(stub).PostAsync(Body, cancel.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("hooks/relative")]
    [InlineData("ftp://hooks.cu.internal/recon-alerts/SECRET-TOKEN")]
    public async Task A_missing_or_unusable_URL_is_refused_without_a_request_and_without_echoing_it(string? url)
    {
        var stub = Stub();
        var options = new ReconAlertWebhookOptions { Url = url is null ? null : new Uri(url, UriKind.RelativeOrAbsolute) };

        var e = await Should.ThrowAsync<ReconAlertConfigurationException>(() => Client(stub, options).PostAsync(Body, TestContext.Current.CancellationToken));

        e.Message.ShouldNotContain("SECRET-TOKEN");
        e.Message.ShouldNotContain("hooks.cu.internal");
        stub.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_setting_that_cannot_be_read_is_a_configuration_error_which_is_not_retried()
    {
        var http = new HttpClient();
        disposables.Add(http);
        var client = new HttpReconAlertClient(http, new ThrowingOptions(), NullLogger<HttpReconAlertClient>.Instance);

        var e = await Should.ThrowAsync<ReconAlertConfigurationException>(() => client.PostAsync(Body, TestContext.Current.CancellationToken));

        e.Message.ShouldNotContain("SECRET-TOKEN");
        e.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task The_production_registration_has_no_retry_and_no_logging_handler_and_logs_and_spans_carry_neither_the_URL_nor_the_token()
    {
        var stub = Stub();
        using var capture = new LogCapture();
        await using var services = Production(stub, capture);
        var ct = TestContext.Current.CancellationToken;

        await services.GetRequiredService<IReconAlertClient>().PostAsync(Body, ct);
        stub.FailNextRequests(1, 503);
        await Should.ThrowAsync<ReconAlertUnavailableException>(() => services.GetRequiredService<IReconAlertClient>().PostAsync(Body, ct));

        stub.Requests.Count.ShouldBe(2, "no handler retried the 503 underneath the RetryPolicy");
        var factory = services.GetRequiredService<IHttpMessageHandlerFactory>();
        HttpMessageHandler handler = factory.CreateHandler(nameof(IReconAlertClient));
        while (handler is DelegatingHandler delegating)
        {
            delegating.GetType().Name.ShouldBe("LifetimeTrackingHttpMessageHandler", "no logging, retry or resilience handler in the pipeline");
            handler = delegating.InnerHandler!;
        }

        var primary = handler.ShouldBeOfType<SocketsHttpHandler>();
        primary.AllowAutoRedirect.ShouldBeFalse();
        primary.UseCookies.ShouldBeFalse();
        primary.ActivityHeadersPropagator.ShouldBeNull("this removes the System.Net.Http span, which would record the whole URL as url.full");
        services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IReconAlertClient)).Timeout.ShouldBe(ReconAlertWebActivity.HttpRequestTimeout);
        capture.Logs.ShouldContain(l => l.Category.Contains(nameof(HttpReconAlertClient), StringComparison.Ordinal), "the client logs; an empty log would pass vacuously");
        capture.Logs.ShouldNotContain(l => l.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal), "the factory's request loggers are removed");
        capture.Findings(Secrets(stub)).ShouldBeEmpty();
        capture.Spans.ShouldNotContain(s => s.Source == "System.Net.Http" && s.Texts.Any(t => t.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Positive_control_a_default_HttpClient_does_put_the_token_into_a_span_so_the_check_above_is_not_vacuous()
    {
        var stub = Stub();
        using var capture = new LogCapture();
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });

        using var response = await http.PostAsync(stub.Url, new StringContent(Body), TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue();
        capture.Findings(Secrets(stub)).ShouldNotBeEmpty("System.Net.Http records the full URL, token included, as url.full; only the query string is redacted");
    }

    [Fact]
    public async Task Probe_the_driver_exceptions_for_a_refused_connection_and_a_timeout_do_not_carry_the_path()
    {
        // Pins what the platform puts in its own exception messages today. The client never attaches or logs them, but if a
        // future runtime added the request path to them, this fails and the decision in ADR 0013 needs another look.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var http = new HttpClient(new SocketsHttpHandler { ActivityHeadersPropagator = null }) { Timeout = TimeSpan.FromSeconds(30) };
        using var shortTimeout = new HttpClient(new SocketsHttpHandler { ActivityHeadersPropagator = null }) { Timeout = TimeSpan.FromMilliseconds(300) };
        var stub = Stub();
        stub.Server.Given(Request.Create().WithPath("/hooks/recon-alerts/" + token).UsingPost()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(5)));

        var refused = await Should.ThrowAsync<HttpRequestException>(() => http.PostAsync(new Uri($"http://127.0.0.1:{port}/hooks/recon-alerts/{token}"), new StringContent(Body), TestContext.Current.CancellationToken));
        var timedOut = await Should.ThrowAsync<TaskCanceledException>(() => shortTimeout.PostAsync(stub.Url, new StringContent(Body), TestContext.Current.CancellationToken));

        string[] texts = [refused.ToString(), timedOut.ToString()];
        texts.ShouldAllBe(t => !t.Contains(token, StringComparison.OrdinalIgnoreCase) && !t.Contains("/hooks/", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        foreach (var d in disposables)
        {
            d.Dispose();
        }
    }

    private AlertWebhookStub Stub()
    {
        var stub = new AlertWebhookStub(token);
        disposables.Add(stub);
        return stub;
    }

    private HttpReconAlertClient Client(AlertWebhookStub? stub, ReconAlertWebhookOptions? options = null, TimeSpan? timeout = null)
    {
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ActivityHeadersPropagator = null }) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        disposables.Add(http);
        options ??= new ReconAlertWebhookOptions { Url = new Uri(stub!.Url) };
        return new HttpReconAlertClient(http, Options.Create(options), NullLogger<HttpReconAlertClient>.Instance);
    }

    private static ServiceProvider Production(AlertWebhookStub stub, LogCapture capture)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{ReconAlertWebhookOptions.SectionName}:{nameof(ReconAlertWebhookOptions.Url)}"] = stub.Url,
        }).Build();
        return new ServiceCollection()
            .AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(capture))
            .AddCreditUnionBatch(configuration)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private IEnumerable<SensitiveValue> Secrets(AlertWebhookStub stub)
    {
        yield return new("the alert webhook token", token);
        yield return new("the alert webhook URL", stub.Url);
        yield return new("the alert webhook path", "/hooks/recon-alerts/");
    }

    private void ShouldNotLeak(string message, AlertWebhookStub stub)
    {
        message.ShouldNotContain(token);
        message.ShouldNotContain(stub.Url);
        message.ShouldNotContain("/hooks/");
        message.ShouldNotContain(stub.RootUrl);
    }

    private sealed class ThrowingOptions : IOptions<ReconAlertWebhookOptions>
    {
        public ReconAlertWebhookOptions Value => throw new InvalidOperationException("Failed to convert configuration value at 'ReconAlertWebhook:Url' (SECRET-TOKEN).");
    }
}
