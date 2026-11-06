using System.Globalization;
using System.Text.Json.Nodes;
using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Map;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.Functions.Orchestrators;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Adf;
using Microsoft.DurableTask;

namespace CreditUnion.Batch.UnitTests.Map;

/// <summary>
/// The ADF JSON is the source of truth for retries, timeouts, the Web activity request and the data flow rule (CLAUDE.md);
/// the code copies them, and these tests keep the copy honest. Every value is read from <c>adf/</c>.
/// </summary>
public sealed class MapPolicyTests
{
    private static readonly MapActivityPolicies Code = MapActivityPolicies.Adf;

    private static readonly string[] AdfNames =
    [
        "Log Map Started", "Get GL Mapping From KeyBridge", "Cache GL Mapping", "Lookup Unmapped Txn Codes",
        "Log Unmapped Txn Codes", "Aggregate Daily GL Totals", "Log Map Succeeded",
    ];

    public static TheoryData<string, Func<MapActivityPolicies, ActivityPolicy>> Activities => new()
    {
        { "Log Map Started", p => p.LogEvent },
        { "Get GL Mapping From KeyBridge", p => p.GetGlMapping },
        { "Cache GL Mapping", p => p.CacheGlMapping },
        { "Lookup Unmapped Txn Codes", p => p.LookupUnmapped },
        { "Log Unmapped Txn Codes", p => p.LogUnmapped },
        { "Aggregate Daily GL Totals", p => p.Aggregate },
        { "Log Map Succeeded", p => p.LogEvent },
    };

    private static JsonNode Pipeline { get; } = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Adf, "pipeline", PipelineNames.MapAndAggregate + ".json")))!;

    private static JsonNode DataFlow { get; } = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Adf, "dataflow", "DF_MapAndAggregateGl.json")))!;

    [Theory]
    [MemberData(nameof(Activities))]
    public void The_code_policy_equals_the_ADF_activity_policy(string activity, Func<MapActivityPolicies, ActivityPolicy> select)
    {
        var adf = AdfActivityPolicy.Read(PipelineNames.MapAndAggregate, activity);
        var code = select(Code);

        code.Retry.ShouldBe(adf.Retry, $"{activity} retry");
        code.RetryInterval.ShouldBe(adf.RetryInterval, $"{activity} retryIntervalInSeconds");
        code.Timeout.ShouldBe(adf.Timeout, $"{activity} timeout");
        code.MaxNumberOfAttempts.ShouldBe(adf.MaxNumberOfAttempts);
    }

    [Theory]
    [MemberData(nameof(Activities))]
    public void The_durable_retry_options_implement_the_ADF_policy_as_retry_plus_one_attempts_at_a_fixed_interval(string activity, Func<MapActivityPolicies, ActivityPolicy> select)
    {
        var adf = AdfActivityPolicy.Read(PipelineNames.MapAndAggregate, activity);

        var options = AdfRetry.Options(select(Code));

        adf.IsImplementedBy(options.Retry!.Policy).ShouldBeTrue(AdfActivityPolicy.Describe(options.Retry.Policy));
    }

    [Fact]
    public void The_KeyBridge_call_has_four_attempts_thirty_seconds_apart_and_the_data_flow_two_attempts_sixty_seconds_apart()
    {
        AdfRetry.Options(Code.GetGlMapping).Retry!.Policy!.MaxNumberOfAttempts.ShouldBe(4);
        AdfRetry.Options(Code.Aggregate).Retry!.Policy!.FirstRetryInterval.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Every_ADF_activity_that_has_a_policy_is_in_the_table_above()
    {
        var withPolicy = new List<string>();
        Collect(Pipeline["properties"]!["activities"]!.AsArray(), withPolicy);

        withPolicy.Order(StringComparer.Ordinal).ShouldBe(AdfNames.Order(StringComparer.Ordinal),
            "a new or renamed ADF activity needs a policy entry in MapActivityPolicies and a row in this table");
    }

    [Fact]
    public void The_orchestrator_mirrors_the_ADF_dependencies()
    {
        // Top level: a single chain, each activity on the previous one's success.
        var top = Pipeline["properties"]!["activities"]!.AsArray().Select(a => a!).ToList();
        top.Select(a => Name(a)).ShouldBe(["Log Map Started", "Get GL Mapping From KeyBridge", "Cache GL Mapping", "Lookup Unmapped Txn Codes", "If Any Txn Code Unmapped", "Log Map Succeeded"]);
        for (var i = 0; i < top.Count; i++)
        {
            Dependencies(top[i]).ShouldBe(i == 0 ? [] : [(Name(top[i - 1]), "Succeeded")], Name(top[i]));
        }

        var condition = top[4]["typeProperties"]!;
        condition["expression"]!["value"]!.GetValue<string>().ShouldBe("@greater(activity('Lookup Unmapped Txn Codes').output.firstRow.UnmappedCount, 0)");
        var ifTrue = condition["ifTrueActivities"]!.AsArray().Select(a => a!).ToList();
        ifTrue.Select(a => Name(a)).ShouldBe(["Log Unmapped Txn Codes", "Fail Txn Code Unmapped"]);
        Dependencies(ifTrue[1]).ShouldBe([("Log Unmapped Txn Codes", "Completed")], "the Fail runs whether or not the log succeeded");
        condition["ifFalseActivities"]!.AsArray().Select(a => Name(a!)).ShouldBe(["Aggregate Daily GL Totals"]);
    }

    [Fact]
    public void The_Fail_activity_and_the_Failed_event_carry_TXN_CODE_UNMAPPED_and_the_message_the_code_builds()
    {
        var ifTrue = Find("If Any Txn Code Unmapped")["typeProperties"]!["ifTrueActivities"]!.AsArray();
        var fail = ifTrue.Single(a => Name(a!) == "Fail Txn Code Unmapped")!["typeProperties"]!;
        var log = ifTrue.Single(a => Name(a!) == "Log Unmapped Txn Codes")!["typeProperties"]!["storedProcedureParameters"]!;
        const string MessageExpression = "@concat('Unmapped transaction codes: ', activity('Lookup Unmapped Txn Codes').output.firstRow.UnmappedCodes)";

        fail["errorCode"]!.GetValue<string>().ShouldBe(PipelineErrorCodes.TxnCodeUnmapped);
        fail["message"]!["value"]!.GetValue<string>().ShouldBe(MessageExpression);
        log["ErrorMessage"]!["value"]!["value"]!.GetValue<string>().ShouldBe(MessageExpression);
        log["ErrorCode"]!["value"]!.GetValue<string>().ShouldBe(PipelineErrorCodes.TxnCodeUnmapped);
        log["Status"]!["value"]!.GetValue<string>().ShouldBe("Failed");
        log["StepName"]!["value"]!.GetValue<string>().ShouldBe("CheckTxnCodeMapping");
        new UnmappedTxnCodes(2, "P2PD,ZZZZ").FailureMessage.ShouldBe("Unmapped transaction codes: P2PD,ZZZZ");
    }

    [Fact]
    public void The_Web_activity_request_matches_the_client()
    {
        var web = Find("Get GL Mapping From KeyBridge");
        var properties = web["typeProperties"]!;

        web["type"]!.GetValue<string>().ShouldBe("WebActivity");
        properties["method"]!.GetValue<string>().ShouldBe("GET");
        properties["url"]!["value"]!.GetValue<string>().ShouldBe($"@concat(pipeline().parameters.keyBridgeBaseUrl, '{KeyBridgeWebActivity.MappingsPath}')");
        properties["headers"]!["Accept"]!.GetValue<string>().ShouldBe("application/json");
        properties["headers"]![KeyBridgeWebActivity.CorrelationHeader]!["value"]!.GetValue<string>().ShouldBe("@pipeline().parameters.batchRunId");
        properties["authentication"]!["type"]!.GetValue<string>().ShouldBe("Basic");
        properties["authentication"]!["username"]!.GetValue<string>().ShouldBe(KeyBridgeWebActivity.Username);
        new KeyBridgeOptions().Username.ShouldBe(KeyBridgeWebActivity.Username, "the options default is the ADF username");
        properties["authentication"]!["password"]!["secretName"]!.GetValue<string>().ShouldBe("keybridge-api-password");
        TimeSpan.Parse(properties["httpRequestTimeout"]!.GetValue<string>(), CultureInfo.InvariantCulture).ShouldBe(KeyBridgeWebActivity.HttpRequestTimeout);
    }

    [Fact]
    public void The_cache_and_lookup_calls_match_the_procedures_and_parameters_in_the_ADF_JSON()
    {
        var cache = Find("Cache GL Mapping")["typeProperties"]!;
        cache["storedProcedureName"]!.GetValue<string>().ShouldBe("[ref].[usp_UpsertTxnCodeGlMap]");
        var parameters = cache["storedProcedureParameters"]!;
        parameters["MappingJson"]!["value"]!["value"]!.GetValue<string>().ShouldBe("@string(activity('Get GL Mapping From KeyBridge').output.mappings)");
        parameters["SourceVersion"]!["value"]!["value"]!.GetValue<string>().ShouldBe("@activity('Get GL Mapping From KeyBridge').output.version");

        var lookup = Find("Lookup Unmapped Txn Codes")["typeProperties"]!;
        lookup["source"]!["sqlReaderStoredProcedureName"]!.GetValue<string>().ShouldBe("[ref].[usp_GetUnmappedTxnCodes]");
        lookup["firstRowOnly"]!.GetValue<bool>().ShouldBeTrue();
        TimeSpan.Parse(lookup["source"]!["queryTimeout"]!.GetValue<string>(), CultureInfo.InvariantCulture).ShouldBe(Code.LookupUnmapped.Timeout);
    }

    [Fact]
    public void The_succeeded_event_names_the_sink_table()
    {
        Find("Log Map Succeeded")["typeProperties"]!["storedProcedureParameters"]!["ObjectName"]!["value"]!.GetValue<string>().ShouldBe("core.DailyGlTotals");
    }

    [Theory]
    [InlineData("query: (concat('SELECT TxnId, TxnCode, PostingDate, Amount FROM core.PostedTransaction WHERE PostingDate = \\'', $businessDate, '\\'')),")]
    [InlineData("Transactions, GlMap join(Transactions@TxnCode == GlMap@TxnCode,")]
    [InlineData("joinType:'inner',")]
    [InlineData("matchType:'exact',")]
    [InlineData("ignoreSpaces: false,")]
    [InlineData("JoinGlMap derive(AllocatedAmount = toDecimal(round(Amount * AllocationRatio, 2, 7), 15, 2)) ~> DeriveAllocatedAmounts")]
    [InlineData("DeriveAllocatedAmounts derive(DebitAmount = iif(DrCr == 'D', AllocatedAmount, toDecimal(0, 15, 2)),")]
    [InlineData("CreditAmount = iif(DrCr == 'C', AllocatedAmount, toDecimal(0, 15, 2))) ~> SplitBySide")]
    [InlineData("SplitBySide aggregate(groupBy(GlAccountNbr),")]
    [InlineData("TxnCount = toInteger(countDistinct(TxnId))) ~> AggregateByGlAccount")]
    [InlineData("NetAmount = toDecimal(DebitTotal - CreditTotal, 19, 2)) ~> DeriveTotals")]
    [InlineData("preSQLs:[(concat('DELETE FROM core.DailyGlTotals WHERE BusinessDate = \\'', $businessDate, '\\''))],")]
    public void The_data_flow_script_still_states_the_rule_the_aggregator_implements(string line)
    {
        // Rounding option 7 is ROUND_HALF_EVEN; ADR 0011 maps each line to DailyGlTotalsAggregator or SqlMapAndAggregateStore.
        DataFlow["properties"]!["typeProperties"]!["scriptLines"]!.AsArray().Select(l => l!.GetValue<string>().Trim()).ShouldContain(line);
    }

    [Fact]
    public void The_data_flow_activity_passes_only_the_business_date()
    {
        var dataflow = Find("Aggregate Daily GL Totals")["typeProperties"]!["dataflow"]!;
        dataflow["referenceName"]!.GetValue<string>().ShouldBe("DF_MapAndAggregateGl");
        dataflow["parameters"]!.AsObject().Select(p => p.Key).ShouldBe(["businessDate"]);
    }

    [Fact]
    public void Failures_that_retrying_cannot_fix_are_not_retried_but_transient_ones_are()
    {
        AdfRetry.IsRetriable(Failure(typeof(KeyBridgeRejectedException))).ShouldBeFalse();
        AdfRetry.IsRetriable(Failure(typeof(KeyBridgeResponseException))).ShouldBeFalse();
        AdfRetry.IsRetriable(Failure(typeof(KeyBridgeConfigurationException))).ShouldBeFalse();
        AdfRetry.IsRetriable(Failure(typeof(GlMappingInvalidException))).ShouldBeFalse();
        AdfRetry.IsRetriable(Failure(typeof(GlAggregationException))).ShouldBeFalse();
        AdfRetry.IsRetriable(Failure(typeof(KeyBridgeUnavailableException))).ShouldBeTrue();
        AdfRetry.IsRetriable(Failure(typeof(TimeoutException))).ShouldBeTrue();
    }

    private static JsonObject Find(string name)
    {
        var found = new List<JsonObject>();
        void Walk(JsonArray activities)
        {
            foreach (var activity in activities.OfType<JsonObject>())
            {
                if (Name(activity) == name)
                {
                    found.Add(activity);
                }

                foreach (var list in new[] { "activities", "ifTrueActivities", "ifFalseActivities" })
                {
                    if (activity["typeProperties"]?[list] is JsonArray nested)
                    {
                        Walk(nested);
                    }
                }
            }
        }

        Walk(Pipeline["properties"]!["activities"]!.AsArray());
        return found.ShouldHaveSingleItem(name);
    }

    private static string Name(JsonNode activity) => activity["name"]!.GetValue<string>();

    private static List<(string Activity, string Condition)> Dependencies(JsonNode activity) =>
        [.. activity["dependsOn"]!.AsArray().SelectMany(d => d!["dependencyConditions"]!.AsArray().Select(c => (d["activity"]!.GetValue<string>(), c!.GetValue<string>())))];

    private static void Collect(JsonArray activities, List<string> names)
    {
        foreach (var activity in activities.OfType<JsonObject>())
        {
            if (activity["policy"] is not null)
            {
                names.Add(Name(activity));
            }

            foreach (var list in new[] { "activities", "ifTrueActivities", "ifFalseActivities" })
            {
                if (activity["typeProperties"]?[list] is JsonArray nested)
                {
                    Collect(nested, names);
                }
            }
        }
    }

    private static TaskFailureDetails Failure(Type type) => new(type.FullName!, "message", null, null, null);
}
