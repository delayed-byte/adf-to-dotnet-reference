using System.Text.Json.Nodes;
using CreditUnion.Batch.Core.Card;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.Functions.Orchestrators;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Adf;
using Microsoft.DurableTask;

namespace CreditUnion.Batch.UnitTests.Card;

/// <summary>The ADF JSON is the source of truth for retries, timeouts and batch count (CLAUDE.md); the code copies them, and these tests keep the copy honest.</summary>
public sealed class CardPolicyTests
{
    private static readonly CardActivityPolicies Code = CardActivityPolicies.Adf;

    private static readonly string[] AdfNames =
    [
        "Log Card Ingest Started", "Reset Card Staging", "List Landing Files", "Copy File To Raw Staging", "Validate Settlement File",
        "Promote Valid File", "Archive Settlement File", "Delete Archived Landing File", "Log File Archived", "Write Error Manifest",
        "Quarantine Settlement File", "Delete Quarantined Landing File", "Log File Quarantined", "Merge Card Settlement To Core", "Log Card Ingest Succeeded",
    ];

    public static TheoryData<string, Func<CardActivityPolicies, ActivityPolicy>> Activities => new()
    {
        { "Log Card Ingest Started", p => p.LogEvent },
        { "Reset Card Staging", p => p.ResetStaging },
        { "List Landing Files", p => p.ListLandingFiles },
        { "Copy File To Raw Staging", p => p.CopyToRawStaging },
        { "Validate Settlement File", p => p.Validate },
        { "Promote Valid File", p => p.Promote },
        { "Archive Settlement File", p => p.Archive },
        { "Delete Archived Landing File", p => p.DeleteArchived },
        { "Log File Archived", p => p.LogArchived },
        { "Write Error Manifest", p => p.WriteManifest },
        { "Quarantine Settlement File", p => p.Quarantine },
        { "Delete Quarantined Landing File", p => p.DeleteQuarantined },
        { "Log File Quarantined", p => p.LogQuarantined },
        { "Merge Card Settlement To Core", p => p.Merge },
        { "Log Card Ingest Succeeded", p => p.LogEvent },
    };

    [Theory]
    [MemberData(nameof(Activities))]
    public void The_code_policy_equals_the_ADF_activity_policy(string activity, Func<CardActivityPolicies, ActivityPolicy> select)
    {
        var adf = AdfActivityPolicy.Read(PipelineNames.CardSettlement, activity);
        var code = select(Code);

        code.Retry.ShouldBe(adf.Retry, $"{activity} retry");
        code.RetryInterval.ShouldBe(adf.RetryInterval, $"{activity} retryIntervalInSeconds");
        code.Timeout.ShouldBe(adf.Timeout, $"{activity} timeout");
        code.MaxNumberOfAttempts.ShouldBe(adf.MaxNumberOfAttempts);
    }

    [Theory]
    [MemberData(nameof(Activities))]
    public void The_durable_retry_options_implement_the_ADF_policy_as_retry_plus_one_attempts_at_a_fixed_interval(string activity, Func<CardActivityPolicies, ActivityPolicy> select)
    {
        var adf = AdfActivityPolicy.Read(PipelineNames.CardSettlement, activity);

        var options = AdfRetry.Options(select(Code));

        adf.IsImplementedBy(options.Retry!.Policy).ShouldBeTrue(AdfActivityPolicy.Describe(options.Retry.Policy));
    }

    [Fact]
    public void Every_ADF_activity_that_has_a_policy_is_in_the_table_above()
    {
        var pipeline = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Adf, "pipeline", PipelineNames.CardSettlement + ".json")))!;
        var withPolicy = new List<string>();
        Collect(pipeline["properties"]!["activities"]!.AsArray(), withPolicy);

        withPolicy.Order(StringComparer.Ordinal).ShouldBe(AdfNames.Order(StringComparer.Ordinal),
            "a new or renamed ADF activity needs a policy entry in CardActivityPolicies and a row in this table");
    }

    [Fact]
    public void The_for_each_batch_count_equals_the_ADF_batch_count()
    {
        var pipeline = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Adf, "pipeline", PipelineNames.CardSettlement + ".json")))!;
        var forEach = pipeline["properties"]!["activities"]!.AsArray().Single(a => a!["name"]!.GetValue<string>() == "For Each Settlement File")!;

        forEach["typeProperties"]!["batchCount"]!.GetValue<int>().ShouldBe(CardActivityPolicies.BatchCount);
        forEach["typeProperties"]!["isSequential"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void The_container_parameter_defaults_equal_the_default_settings()
    {
        var pipeline = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Adf, "pipeline", PipelineNames.CardSettlement + ".json")))!;
        var parameters = pipeline["properties"]!["parameters"]!;
        string Default(string name) => parameters[name]!["defaultValue"]!.GetValue<string>();

        CardSettlementSettings.Defaults.ShouldBe(new CardSettlementSettings(
            Default("landingContainer"), Default("archiveContainer"), Default("quarantineContainer"), Default("retainUntil")));
    }

    [Fact]
    public void A_file_data_error_is_not_retried_but_transient_failures_are()
    {
        AdfRetry.IsRetriable(Failure(typeof(CardFileDataException))).ShouldBeFalse();
        AdfRetry.IsRetriable(Failure(typeof(TimeoutException))).ShouldBeTrue();
        AdfRetry.IsRetriable(Failure(typeof(InvalidOperationException))).ShouldBeTrue();
    }

    private static void Collect(JsonArray activities, List<string> names)
    {
        foreach (var activity in activities.OfType<JsonObject>())
        {
            if (activity["policy"] is not null)
            {
                names.Add(activity["name"]!.GetValue<string>());
            }

            if (activity["typeProperties"] is not JsonObject properties)
            {
                continue;
            }

            foreach (var list in new[] { "activities", "ifTrueActivities", "ifFalseActivities" })
            {
                if (properties[list] is JsonArray nested)
                {
                    Collect(nested, names);
                }
            }
        }
    }

    private static TaskFailureDetails Failure(Type type) => new(type.FullName!, "message", null, null, null);
}
