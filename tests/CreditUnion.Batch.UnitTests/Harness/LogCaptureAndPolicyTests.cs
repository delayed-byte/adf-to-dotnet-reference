// Plain logger calls on purpose: the test checks what reaches the provider, not logging performance.
#pragma warning disable CA1848, CA1873

using System.Diagnostics;
using CreditUnion.Batch.TestHarness.Adf;
using CreditUnion.Batch.TestHarness.Logging;
using Microsoft.DurableTask;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreditUnion.Batch.UnitTests.Harness;

/// <summary>Self-tests of the AT-17 log and trace scanner and of the ADF retry-policy reader used by AT-04 and AT-11.</summary>
public sealed class LogCaptureAndPolicyTests
{
    private const string AccountNumber = "990000300001";
    private const string CardNumber = "4000001234";
    private static readonly SensitiveValue Secret = new("the KeyBridge password", "Aa1-s3cretValue");
    private static readonly Dictionary<string, string> Numbers = new(StringComparer.Ordinal)
    {
        [AccountNumber] = "account number ending 0001",
        [CardNumber] = "card number ending 1234",
    };

    [Fact]
    public void Findings_cover_messages_structured_values_scopes_exceptions_and_spans_and_never_echo_the_value()
    {
        using var capture = new LogCapture();
        using var services = new ServiceCollection().AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(capture)).BuildServiceProvider();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Test");
        using var source = new ActivitySource("CreditUnion.Batch.UnitTests.LogCapture");

        logger.LogInformation("Clean line for account ********0001 and card 400000******1234 on 20261104");
        logger.LogDebug("Connecting with {ConnectionString}", "Server=x;PWD=Aa1-s3cretValue");
        using (logger.BeginScope(new Dictionary<string, object?> { ["Account"] = AccountNumber }))
        {
            logger.LogTrace("Inside a scope");
        }

        logger.LogError(new InvalidOperationException("wrapped", new IOException("io for AA1-S3CRETVALUE")), "Failed");
        using (var span = source.StartActivity("call"))
        {
            span!.SetTag("url.full", "https://hooks.example/x" + AccountNumber + "7");
        }

        capture.AddHostText("orchestration failure", "card " + CardNumber);

        var findings = capture.Findings([Secret], Numbers);

        // LogCapture listens to every ActivitySource in the process, so a span from a test running at the same time
        // (an HttpClient call) can land in this capture. Count only this test's own spans.
        var ownSpan = capture.Spans.Select((s, i) => (s.Source, Number: i + 1)).Single(s => s.Source == source.Name).Number;
        findings = [.. findings.Where(f => !f.StartsWith("span #", StringComparison.Ordinal) || f.Contains($"[{source.Name} ", StringComparison.Ordinal))];

        findings.ShouldBe(
        [
            "log #2 [Debug Test] contains the KeyBridge password",
            "log #3 [Trace Test] contains account number ending 0001",
            "log #4 [Error Test] contains the KeyBridge password",
            $"span #{ownSpan} [CreditUnion.Batch.UnitTests.LogCapture 'call'] contains account number ending 0001",
            "orchestration failure contains card number ending 1234",
        ], ignoreOrder: true);
        findings.ShouldAllBe(f => !f.Contains(Secret.Value, StringComparison.OrdinalIgnoreCase) && !f.Contains(AccountNumber, StringComparison.Ordinal));
        capture.Logs.Count.ShouldBe(4);
    }

    [Fact]
    public void ADF_retry_policies_are_read_from_the_pipeline_json_including_nested_activities()
    {
        var copy = AdfActivityPolicy.Read("PL_ExtractKeyStone", "Copy Db2 To Staging");
        copy.ShouldBe(new AdfActivityPolicy("PL_ExtractKeyStone", "Copy Db2 To Staging", 3, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1)));
        copy.MaxNumberOfAttempts.ShouldBe(4);

        var web = AdfActivityPolicy.Read("PL_MapAndAggregate", "Get GL Mapping From KeyBridge");
        (web.Retry, web.RetryInterval).ShouldBe((3, TimeSpan.FromSeconds(30)));

        AdfActivityPolicy.Read("PL_IngestCardSettlement", "Promote Valid File").Retry.ShouldBe(2);
        Should.Throw<InvalidOperationException>(() => AdfActivityPolicy.Read("PL_ExtractKeyStone", "No Such Activity"));
    }

    [Fact]
    public void A_Durable_policy_implements_an_ADF_policy_only_with_the_same_attempts_and_a_fixed_interval()
    {
        var adf = AdfActivityPolicy.Read("PL_ExtractKeyStone", "Copy Db2 To Staging");

        adf.IsImplementedBy(new RetryPolicy(4, TimeSpan.FromSeconds(30))).ShouldBeTrue();
        adf.IsImplementedBy(new RetryPolicy(3, TimeSpan.FromSeconds(30))).ShouldBeFalse("ADF retry 3 means 4 attempts");
        adf.IsImplementedBy(new RetryPolicy(4, TimeSpan.FromSeconds(5))).ShouldBeFalse();
        adf.IsImplementedBy(new RetryPolicy(4, TimeSpan.FromSeconds(30), backoffCoefficient: 2)).ShouldBeFalse("ADF waits a fixed interval");
        adf.IsImplementedBy(null).ShouldBeFalse();
    }
}
