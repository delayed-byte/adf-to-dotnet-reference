using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.Core.Map;
using CreditUnion.Batch.Functions;
using CreditUnion.Batch.KeyStone.KeyBridge;
using CreditUnion.Batch.TestHarness.Logging;
using CreditUnion.Batch.TestHarness.Scenarios;
using CreditUnion.Batch.TestHarness.Stubs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace CreditUnion.Batch.UnitTests.Map;

/// <summary>
/// The Web activity "Get GL Mapping From KeyBridge" against the in-process KeyBridge stub (ADR 0012): the request, the
/// classification of every failure for the RetryPolicy, and that neither the password, the Basic credential nor the
/// URL reaches an exception message, a log line or a span. One call is always exactly one HTTP request.
/// </summary>
public sealed class KeyBridgeClientTests : IDisposable
{
    private static readonly Guid RunId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly string ContractJson = Scenario.Load("standard-day").KeyBridgeMappingJson;

    private readonly string password = "kb-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    private readonly List<IDisposable> disposables = [];

    private string BasicCredential => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{KeyBridgeStub.Username}:{password}"));

    [Fact]
    public async Task It_sends_one_GET_with_Accept_the_correlation_id_and_Basic_credentials_and_returns_version_and_mappings()
    {
        var stub = Stub(ContractJson);

        var result = await Client(stub).GetGlMappingsAsync(RunId, TestContext.Current.CancellationToken);

        var request = stub.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe("GET");
        request.Path.ShouldBe(KeyBridgeStub.MappingsPath);
        request.Headers["Accept"].ShouldBe("application/json");
        request.Headers[KeyBridgeWebActivity.CorrelationHeader].ShouldBe(RunId.ToString("D"));
        request.Headers["Authorization"].ShouldBe("Basic " + BasicCredential);
        var contract = JsonNode.Parse(ContractJson)!;
        result.Version.ShouldBe(contract["version"]!.GetValue<string>());
        JsonNode.DeepEquals(JsonNode.Parse(result.MappingsJson), contract["mappings"]).ShouldBeTrue("output.mappings passes through unchanged");
    }

    [Theory]
    [InlineData(503)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(504)]
    [InlineData(429)]
    [InlineData(408)]
    public async Task Transient_statuses_raise_KeyBridgeUnavailable_which_the_policy_retries(int status)
    {
        var stub = Stub(ContractJson);
        stub.FailNextRequests(1, status);

        var e = await Should.ThrowAsync<KeyBridgeUnavailableException>(() => Client(stub).GetGlMappingsAsync(RunId, TestContext.Current.CancellationToken));

        e.Message.ShouldContain(status.ToString(System.Globalization.CultureInfo.InvariantCulture));
        stub.Requests.Count.ShouldBe(1, "the client never retries; the RetryPolicy does");
        ShouldNotLeak(e.Message, stub);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(302)]
    public async Task Other_statuses_raise_KeyBridgeRejected_which_is_not_retried(int status)
    {
        var stub = Stub(ContractJson);
        stub.FailNextRequests(1, status);

        var e = await Should.ThrowAsync<KeyBridgeRejectedException>(() => Client(stub).GetGlMappingsAsync(RunId, TestContext.Current.CancellationToken));

        stub.Requests.Count.ShouldBe(1, "a redirect is not followed, so the credential goes to the configured host only");
        ShouldNotLeak(e.Message, stub);
    }

    [Fact]
    public async Task A_wrong_password_gets_401_which_is_rejected_without_naming_either_password()
    {
        var stub = Stub(ContractJson);

        var e = await Should.ThrowAsync<KeyBridgeRejectedException>(() =>
            Client(stub, new KeyBridgeOptions { BaseUrl = new Uri(stub.BaseUrl), Password = "wrong-" + password }).GetGlMappingsAsync(RunId, TestContext.Current.CancellationToken));

        e.Message.ShouldContain("401");
        e.Message.ShouldNotContain("wrong-", Case.Insensitive);
        ShouldNotLeak(e.Message, stub);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"version\":\"2026-11-01\"}")]
    [InlineData("[{\"mappings\":[]}]")]
    public async Task A_2xx_body_without_mappings_raises_KeyBridgeResponse(string body)
    {
        var stub = Stub(body);

        var e = await Should.ThrowAsync<KeyBridgeResponseException>(() => Client(stub).GetGlMappingsAsync(RunId, TestContext.Current.CancellationToken));

        e.Message.ShouldNotContain(body);
        ShouldNotLeak(e.Message, stub);
    }

    [Fact]
    public async Task A_missing_or_null_version_is_null_and_any_mappings_value_is_passed_on_for_the_procedure_to_judge()
    {
        var result = await Client(Stub("{\"mappings\":{\"a\":1}}")).GetGlMappingsAsync(RunId, TestContext.Current.CancellationToken);
        var nullVersion = await Client(Stub("{\"version\":null,\"mappings\":[]}")).GetGlMappingsAsync(RunId, TestContext.Current.CancellationToken);

        result.ShouldBe(new KeyBridgeGlMappings(null, "{\"a\":1}"));
        nullVersion.ShouldBe(new KeyBridgeGlMappings(null, "[]"));
    }

    [Fact]
    public async Task A_response_over_the_4_MB_Web_activity_limit_raises_KeyBridgeResponse()
    {
        var big = "{\"version\":\"v\",\"mappings\":[\"" + new string('x', KeyBridgeWebActivity.MaxResponseBytes) + "\"]}";

        await Should.ThrowAsync<KeyBridgeResponseException>(() => Client(Stub(big)).GetGlMappingsAsync(RunId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task No_answer_within_the_http_request_timeout_raises_TimeoutException()
    {
        var stub = Stub(ContractJson);
        stub.Server.Given(Request.Create().WithPath(KeyBridgeStub.MappingsPath).UsingGet()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(ContractJson).WithDelay(TimeSpan.FromSeconds(5)));

        var e = await Should.ThrowAsync<TimeoutException>(() => Client(stub, timeout: TimeSpan.FromMilliseconds(300)).GetGlMappingsAsync(RunId, TestContext.Current.CancellationToken));

        e.Message.ShouldContain("httpRequestTimeout");
    }

    [Fact]
    public async Task A_refused_connection_raises_KeyBridgeUnavailable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var options = new KeyBridgeOptions { BaseUrl = new Uri($"http://127.0.0.1:{port}/api"), Password = password };

        var e = await Should.ThrowAsync<KeyBridgeUnavailableException>(() => Client(null, options).GetGlMappingsAsync(RunId, TestContext.Current.CancellationToken));

        e.Message.ShouldNotContain(password);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_turned_into_a_timeout()
    {
        var stub = Stub(ContractJson);
        stub.Server.Given(Request.Create().WithPath(KeyBridgeStub.MappingsPath).UsingGet()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(ContractJson).WithDelay(TimeSpan.FromSeconds(5)));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(() => Client(stub).GetGlMappingsAsync(RunId, cancel.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("api/relative")]
    [InlineData("ftp://keybridge.cu.internal/api")]
    [InlineData("https://cu-batch:SECRET-IN-URL@keybridge.cu.internal/api")]
    [InlineData("https://keybridge.cu.internal/api?token=SECRET-IN-URL")]
    public async Task Unsafe_or_missing_base_urls_are_refused_without_a_request_and_without_echoing_the_url(string? baseUrl)
    {
        var stub = Stub(ContractJson);
        var options = new KeyBridgeOptions { BaseUrl = baseUrl is null ? null : new Uri(baseUrl, UriKind.RelativeOrAbsolute), Password = password };

        var e = await Should.ThrowAsync<KeyBridgeConfigurationException>(() => Client(stub, options).GetGlMappingsAsync(RunId, TestContext.Current.CancellationToken));

        e.Message.ShouldNotContain("SECRET-IN-URL");
        e.Message.ShouldNotContain("keybridge.cu.internal");
        stub.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("cu-batch", "")]
    [InlineData("", "p")]
    [InlineData("cu:batch", "p")]
    public async Task Missing_credentials_are_refused_without_a_request(string username, string secret)
    {
        var stub = Stub(ContractJson);

        await Should.ThrowAsync<KeyBridgeConfigurationException>(() =>
            Client(stub, new KeyBridgeOptions { BaseUrl = new Uri(stub.BaseUrl), Username = username, Password = secret }).GetGlMappingsAsync(RunId, TestContext.Current.CancellationToken));

        stub.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("https://keybridge.cu.internal/api", "https://keybridge.cu.internal/api/v1/reference/transaction-codes/gl-mappings")]
    [InlineData("http://127.0.0.1:8081/api", "http://127.0.0.1:8081/api/v1/reference/transaction-codes/gl-mappings")]
    public void The_request_url_is_the_ADF_concat_of_the_base_url_and_the_path(string baseUrl, string expected)
    {
        HttpKeyBridgeClient.RequestUri(new KeyBridgeOptions { BaseUrl = new Uri(baseUrl) }).ToString().ShouldBe(expected);
    }

    [Fact]
    public async Task The_production_registration_has_no_retry_and_no_logging_handler_and_logs_and_spans_carry_no_secret()
    {
        var stub = Stub(ContractJson);
        using var capture = new LogCapture();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{KeyBridgeOptions.SectionName}:{nameof(KeyBridgeOptions.BaseUrl)}"] = stub.BaseUrl,
            [$"{KeyBridgeOptions.SectionName}:{nameof(KeyBridgeOptions.Password)}"] = password,
        }).Build();
        await using var services = new ServiceCollection()
            .AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(capture))
            .AddCreditUnionBatch(configuration)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var ct = TestContext.Current.CancellationToken;

        await services.GetRequiredService<IKeyBridgeClient>().GetGlMappingsAsync(RunId, ct);
        stub.FailNextRequests(1, 503);
        await Should.ThrowAsync<KeyBridgeUnavailableException>(() => services.GetRequiredService<IKeyBridgeClient>().GetGlMappingsAsync(RunId, ct));

        stub.Requests.Count.ShouldBe(2, "no handler retried the 503 underneath the RetryPolicy");
        var factory = services.GetRequiredService<IHttpMessageHandlerFactory>();
        HttpMessageHandler handler = factory.CreateHandler(nameof(IKeyBridgeClient));
        while (handler is DelegatingHandler delegating)
        {
            delegating.GetType().Name.ShouldBe("LifetimeTrackingHttpMessageHandler", "no logging, retry or resilience handler in the pipeline");
            handler = delegating.InnerHandler!;
        }

        var primary = handler.ShouldBeOfType<SocketsHttpHandler>();
        primary.AllowAutoRedirect.ShouldBeFalse();
        primary.UseCookies.ShouldBeFalse();
        services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IKeyBridgeClient)).Timeout.ShouldBe(KeyBridgeWebActivity.HttpRequestTimeout);
        capture.Logs.ShouldNotBeEmpty();
        capture.Logs.ShouldNotContain(l => l.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal), "the factory's request loggers are removed");
        capture.Findings([new SensitiveValue("the KeyBridge password", password), new SensitiveValue("the KeyBridge Basic credential", BasicCredential)]).ShouldBeEmpty();
    }

    public void Dispose()
    {
        foreach (var d in disposables)
        {
            d.Dispose();
        }
    }

    private KeyBridgeStub Stub(string body)
    {
        var stub = new KeyBridgeStub(body, password);
        disposables.Add(stub);
        return stub;
    }

    private HttpKeyBridgeClient Client(KeyBridgeStub? stub, KeyBridgeOptions? options = null, TimeSpan? timeout = null)
    {
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        disposables.Add(http);
        options ??= new KeyBridgeOptions { BaseUrl = new Uri(stub!.BaseUrl), Password = password };
        return new HttpKeyBridgeClient(http, Options.Create(options), NullLogger<HttpKeyBridgeClient>.Instance);
    }

    private void ShouldNotLeak(string message, KeyBridgeStub stub)
    {
        message.ShouldNotContain(password);
        message.ShouldNotContain(BasicCredential);
        message.ShouldNotContain(stub.RootUrl);
    }
}
