using System.Text.Json.Nodes;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.Functions.Orchestrators;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Adf;
using Microsoft.DurableTask;

namespace CreditUnion.Batch.UnitTests.Extract;

/// <summary>The ADF JSON is the source of truth for retries, timeouts and batch count (CLAUDE.md); the code copies them, and these tests keep the copy honest.</summary>
public sealed class ExtractPolicyTests
{
    private static readonly ExtractActivityPolicies Code = ExtractActivityPolicies.Adf;

    public static TheoryData<string, Func<ExtractActivityPolicies, ActivityPolicy>> Activities => new()
    {
        { "Log Extract Started", p => p.LogEvent },
        { "Log Staging Row Count", p => p.LogEvent },
        { "Log Extract Succeeded", p => p.LogEvent },
        { "Lookup Extract Config", p => p.LookupConfig },
        { "Copy Db2 To Staging", p => p.CopyToStaging },
        { "Merge KeyStone To Core", p => p.Merge },
    };

    [Theory]
    [MemberData(nameof(Activities))]
    public void The_code_policy_equals_the_ADF_activity_policy(string activity, Func<ExtractActivityPolicies, ActivityPolicy> select)
    {
        var adf = AdfActivityPolicy.Read(PipelineNames.Extract, activity);
        var code = select(Code);

        code.Retry.ShouldBe(adf.Retry, $"{activity} retry");
        code.RetryInterval.ShouldBe(adf.RetryInterval, $"{activity} retryIntervalInSeconds");
        code.Timeout.ShouldBe(adf.Timeout, $"{activity} timeout");
        code.MaxNumberOfAttempts.ShouldBe(adf.MaxNumberOfAttempts);
    }

    [Fact]
    public void The_for_each_batch_count_equals_the_ADF_batch_count()
    {
        var pipeline = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Adf, "pipeline", PipelineNames.Extract + ".json")))!;
        var forEach = pipeline["properties"]!["activities"]!.AsArray().Single(a => a!["name"]!.GetValue<string>() == "For Each Source Table")!;

        forEach["typeProperties"]!["batchCount"]!.GetValue<int>().ShouldBe(ExtractActivityPolicies.BatchCount);
        forEach["typeProperties"]!["isSequential"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(Activities))]
    public void The_durable_retry_options_implement_the_ADF_policy_as_retry_plus_one_attempts_at_a_fixed_interval(string activity, Func<ExtractActivityPolicies, ActivityPolicy> select)
    {
        var adf = AdfActivityPolicy.Read(PipelineNames.Extract, activity);

        var options = AdfRetry.Options(select(Code));

        adf.IsImplementedBy(options.Retry!.Policy).ShouldBeTrue(AdfActivityPolicy.Describe(options.Retry.Policy));
    }

    [Fact]
    public void Data_errors_and_configuration_errors_are_not_retried_but_transient_failures_are()
    {
        AdfRetry.IsRetriable(Failure(typeof(ExtractDataException))).ShouldBeFalse();
        AdfRetry.IsRetriable(Failure(typeof(ExtractConfigurationException))).ShouldBeFalse();
        AdfRetry.IsRetriable(Failure(typeof(TimeoutException))).ShouldBeTrue();
        AdfRetry.IsRetriable(Failure(typeof(InvalidOperationException))).ShouldBeTrue();
    }

    private static TaskFailureDetails Failure(Type type) => new(type.FullName!, "message", null, null, null);
}
