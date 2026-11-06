using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.Core.Reconciliation;
using CreditUnion.Batch.Functions.Orchestrators;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Adf;
using Microsoft.DurableTask;

namespace CreditUnion.Batch.UnitTests.Reconciliation;

/// <summary>
/// The ADF JSON is the source of truth for retries, timeouts, the Web activities and the expressions (CLAUDE.md); the code
/// copies them, and these tests keep the copy honest. Every value is read from <c>adf/pipeline/PL_Reconciliation.json</c>.
/// </summary>
public sealed partial class ReconPolicyTests
{
    private static readonly ReconActivityPolicies Code = ReconActivityPolicies.Adf;

    /// <summary>Every ADF activity with a policy, including those in the If Condition. "Get Alert Webhook Url" is configuration in .NET (ADR 0013).</summary>
    private static readonly string[] AdfNames =
    [
        "Log Recon Started", "Lookup Card Totals", "Lookup GL Totals", "Record Recon Exceptions", "Get Alert Webhook Url",
        "Post Recon Alert", "Mark Recon Alert Sent", "Log Recon Outcome",
    ];

    public static TheoryData<string, Func<ReconActivityPolicies, ActivityPolicy>> Activities => new()
    {
        { "Log Recon Started", p => p.LogEvent },
        { "Lookup Card Totals", p => p.CardTotals },
        { "Lookup GL Totals", p => p.GlTotals },
        { "Record Recon Exceptions", p => p.RecordExceptions },
        { "Post Recon Alert", p => p.PostAlert },
        { "Mark Recon Alert Sent", p => p.MarkAlertSent },
        { "Log Recon Outcome", p => p.LogEvent },
    };

    private static JsonNode Pipeline { get; } = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Adf, "pipeline", PipelineNames.Reconciliation + ".json")))!;

    [Theory]
    [MemberData(nameof(Activities))]
    public void The_code_policy_equals_the_ADF_activity_policy(string activity, Func<ReconActivityPolicies, ActivityPolicy> select)
    {
        var adf = AdfActivityPolicy.Read(PipelineNames.Reconciliation, activity);
        var code = select(Code);

        code.Retry.ShouldBe(adf.Retry, $"{activity} retry");
        code.RetryInterval.ShouldBe(adf.RetryInterval, $"{activity} retryIntervalInSeconds");
        code.Timeout.ShouldBe(adf.Timeout, $"{activity} timeout");
        code.MaxNumberOfAttempts.ShouldBe(adf.MaxNumberOfAttempts);
    }

    [Theory]
    [MemberData(nameof(Activities))]
    public void The_durable_retry_options_implement_the_ADF_policy_as_retry_plus_one_attempts_at_a_fixed_interval(string activity, Func<ReconActivityPolicies, ActivityPolicy> select)
    {
        var adf = AdfActivityPolicy.Read(PipelineNames.Reconciliation, activity);

        var options = AdfRetry.Options(select(Code));

        adf.IsImplementedBy(options.Retry!.Policy).ShouldBeTrue(AdfActivityPolicy.Describe(options.Retry.Policy));
    }

    [Fact]
    public void Post_Recon_Alert_has_four_attempts_thirty_seconds_apart_and_a_two_minute_activity_timeout()
    {
        AdfRetry.Options(Code.PostAlert).Retry!.Policy!.MaxNumberOfAttempts.ShouldBe(4);
        AdfRetry.Options(Code.PostAlert).Retry!.Policy!.FirstRetryInterval.ShouldBe(TimeSpan.FromSeconds(30));
        Code.PostAlert.Timeout.ShouldBe(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void Every_ADF_activity_that_has_a_policy_is_in_the_table_above()
    {
        var withPolicy = new List<string>();
        Collect(Pipeline["properties"]!["activities"]!.AsArray(), withPolicy);

        withPolicy.Order(StringComparer.Ordinal).ShouldBe(AdfNames.Order(StringComparer.Ordinal),
            "a new or renamed ADF activity needs a policy entry in ReconActivityPolicies and a row in this table");
    }

    [Fact]
    public void The_orchestrator_mirrors_the_ADF_activities_and_dependencies()
    {
        var top = Pipeline["properties"]!["activities"]!.AsArray().Select(a => a!).ToList();
        top.Select(a => Name(a)).ShouldBe(["Log Recon Started", "Lookup Card Totals", "Lookup GL Totals", "Record Recon Exceptions", "If Alert Required", "Log Recon Outcome"]);
        Dependencies(top[0]).ShouldBeEmpty();
        Dependencies(top[1]).ShouldBe([("Log Recon Started", "Succeeded")], "both lookups depend only on the Started event, so they run together");
        Dependencies(top[2]).ShouldBe([("Log Recon Started", "Succeeded")]);
        Dependencies(top[3]).ShouldBe([("Lookup Card Totals", "Succeeded"), ("Lookup GL Totals", "Succeeded")]);
        Dependencies(top[4]).ShouldBe([("Record Recon Exceptions", "Succeeded")]);
        Dependencies(top[5]).ShouldBe([("If Alert Required", "Succeeded")]);

        var condition = top[4]["typeProperties"]!;
        condition["expression"]!["value"]!.GetValue<string>().ShouldBe("@equals(activity('Record Recon Exceptions').output.firstRow.AlertRequired, true)");
        var ifTrue = condition["ifTrueActivities"]!.AsArray().Select(a => a!).ToList();
        ifTrue.Select(a => Name(a)).ShouldBe(["Get Alert Webhook Url", "Post Recon Alert", "Mark Recon Alert Sent"]);
        Dependencies(ifTrue[0]).ShouldBeEmpty();
        Dependencies(ifTrue[1]).ShouldBe([("Get Alert Webhook Url", "Succeeded")]);
        Dependencies(ifTrue[2]).ShouldBe([("Post Recon Alert", "Succeeded")], "the alert is marked sent only after the POST succeeded");
        condition["ifFalseActivities"]!.AsArray().ShouldBeEmpty("a false condition does nothing");
    }

    [Fact]
    public void The_lookups_call_the_procedures_with_the_parameters_and_timeouts_the_code_passes()
    {
        foreach (var (name, procedure, parameters, timeout) in new (string, string, string[], TimeSpan)[]
        {
            ("Lookup Card Totals", "[ops].[usp_GetCardReconTotals]", ["BusinessDate", "Tolerance"], Code.CardTotals.Timeout),
            ("Lookup GL Totals", "[ops].[usp_GetGlReconTotals]", ["BusinessDate", "Tolerance"], Code.GlTotals.Timeout),
            ("Record Recon Exceptions", "[ops].[usp_RecordReconExceptions]", ["BusinessDate", "BatchRunId", "Tolerance"], Code.RecordExceptions.Timeout),
        })
        {
            var lookup = Find(name)["typeProperties"]!;
            lookup["firstRowOnly"]!.GetValue<bool>().ShouldBeTrue(name);
            lookup["source"]!["sqlReaderStoredProcedureName"]!.GetValue<string>().ShouldBe(procedure);
            lookup["source"]!["storedProcedureParameters"]!.AsObject().Select(p => p.Key).ShouldBe(parameters, ignoreOrder: true, name);
            TimeSpan.Parse(lookup["source"]!["queryTimeout"]!.GetValue<string>(), CultureInfo.InvariantCulture).ShouldBe(timeout, $"{name} queryTimeout");
        }

        var tolerance = Find("Lookup Card Totals")["typeProperties"]!["source"]!["storedProcedureParameters"]!["Tolerance"]!;
        tolerance["type"]!.GetValue<string>().ShouldBe("Decimal");
        tolerance["value"]!["value"]!.GetValue<string>().ShouldBe("@pipeline().parameters.tolerance");
        var mark = Find("Mark Recon Alert Sent")["typeProperties"]!;
        mark["storedProcedureName"]!.GetValue<string>().ShouldBe("[ops].[usp_MarkReconAlertSent]");
        mark["storedProcedureParameters"]!.AsObject().Select(p => p.Key).ShouldBe(["BusinessDate", "BatchRunId"]);
    }

    [Fact]
    public void The_tolerance_parameter_defaults_to_the_string_0_00_which_is_the_input_default()
    {
        var parameters = Pipeline["properties"]!["parameters"]!;
        parameters["tolerance"]!["type"]!.GetValue<string>().ShouldBe("string");
        parameters["tolerance"]!["defaultValue"]!.GetValue<string>().ShouldBe("0.00");
        parameters.AsObject().Select(p => p.Key).ShouldBe(["businessDate", "batchRunId", "tolerance"]);
        decimal.Parse(parameters["tolerance"]!["defaultValue"]!.GetValue<string>(), NumberStyles.Number, CultureInfo.InvariantCulture).ShouldBe(0.00m);
        new NightlyBatchInput().ReconTolerance.ShouldBe(0.00m);
        ReconFormat.Money(new NightlyBatchInput().ReconTolerance).ShouldBe("0.00");
    }

    [Fact]
    public void Get_Alert_Webhook_Url_is_a_Key_Vault_read_with_a_secure_output_that_configuration_replaces()
    {
        var get = Find("Get Alert Webhook Url");
        var properties = get["typeProperties"]!;

        get["type"]!.GetValue<string>().ShouldBe("WebActivity");
        get["policy"]!["secureOutput"]!.GetValue<bool>().ShouldBeTrue("the secret URL is never logged by ADF; in .NET it never leaves the posting activity");
        properties["method"]!.GetValue<string>().ShouldBe("GET");
        properties["url"]!.GetValue<string>().ShouldEndWith($"/secrets/{ReconAlertWebActivity.KeyVaultSecretName}?api-version=7.4");
        properties["authentication"]!["type"]!.GetValue<string>().ShouldBe("MSI");
        properties["authentication"]!["resource"]!.GetValue<string>().ShouldBe("https://vault.azure.net");
        ReconAlertWebhookOptions.SectionName.ShouldBe("ReconAlertWebhook");
    }

    [Fact]
    public void The_Post_Recon_Alert_request_matches_the_client()
    {
        var post = Find("Post Recon Alert");
        var properties = post["typeProperties"]!;

        post["type"]!.GetValue<string>().ShouldBe("WebActivity");
        post["policy"]!["secureInput"]!.GetValue<bool>().ShouldBeTrue("the input embeds the secret URL");
        properties["method"]!.GetValue<string>().ShouldBe("POST");
        properties["url"]!["value"]!.GetValue<string>().ShouldBe("@activity('Get Alert Webhook Url').output.value");
        properties["headers"]!.AsObject().Select(h => (h.Key, h.Value!.GetValue<string>())).ShouldBe([("Content-Type", ReconAlertWebActivity.ContentType)]);
        TimeSpan.Parse(properties["httpRequestTimeout"]!.GetValue<string>(), CultureInfo.InvariantCulture).ShouldBe(ReconAlertWebActivity.HttpRequestTimeout);
        properties["authentication"].ShouldBeNull("the token in the URL is the only credential");
    }

    [Fact]
    public void The_alert_body_is_the_ADF_body_expression_with_its_values_filled_in()
    {
        var expression = Find("Post Recon Alert")["typeProperties"]!["body"]!["value"]!.GetValue<string>();
        var alert = new ReconAlert(new DateOnly(2026, 11, 4), Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), -12.50m, 3, 4, "CardSettlement ALL difference -12.50; GeneralLedger 1010:DEBIT difference 0.01");
        var values = new Dictionary<string, string>
        {
            ["pipeline().parameters.businessDate"] = "2026-11-04",
            ["pipeline().parameters.batchRunId"] = "0f8fad5b-d9cb-469f-a165-70867728950e",
            ["activity('Lookup Card Totals').output.firstRow.Difference"] = "-12.50",
            ["activity('Lookup GL Totals').output.firstRow.ItemsOutOfTolerance"] = "3",
            ["activity('Record Recon Exceptions').output.firstRow.ExceptionCount"] = "4",
            ["activity('Record Recon Exceptions').output.firstRow.AlertSummary"] = alert.Summary,
        };

        var interpolated = Interpolation().Replace(expression, m => values[m.Groups["e"].Value]);

        ReconFormat.AlertBody(alert).ShouldBe(interpolated, "same properties, same order, same number formatting, no whitespace");
        Interpolation().Matches(expression).Select(m => m.Groups["e"].Value).ShouldBe(values.Keys, "the body reads exactly these six values");
    }

    [Fact]
    public void The_notes_status_and_record_count_are_the_ADF_expressions()
    {
        var parameters = Find("Log Recon Outcome")["typeProperties"]!["storedProcedureParameters"]!;
        var notes = parameters["Notes"]!["value"]!["value"]!.GetValue<string>();
        var literals = Regex.Matches(notes, "'(?<s>[^']*)'").Select(m => m.Groups["s"].Value).Where(s => s.Contains('=', StringComparison.Ordinal)).ToList();
        literals.Count.ShouldBe(3, "the three text pieces of the concat, not the activity names");

        notes.ShouldBe("@concat('card difference=', string(activity('Lookup Card Totals').output.firstRow.Difference), '; GL items out of tolerance=', string(activity('Lookup GL Totals').output.firstRow.ItemsOutOfTolerance), '; tolerance=', pipeline().parameters.tolerance)");
        ReconFormat.Notes(0.01m, 2, 0.00m).ShouldBe($"{literals[0]}0.01{literals[1]}2{literals[2]}0.00");
        parameters["Status"]!["value"]!["value"]!.GetValue<string>().ShouldBe("@if(equals(activity('Record Recon Exceptions').output.firstRow.IsOutOfTolerance, true), 'Exceptions', 'Reconciled')");
        (ReconFormat.Status(true), ReconFormat.Status(false)).ShouldBe(("Exceptions", "Reconciled"));
        parameters["RecordCount"]!["value"]!["value"]!.GetValue<string>().ShouldBe("@activity('Record Recon Exceptions').output.firstRow.ExceptionCount");
        parameters["RecordCount"]!["type"]!.GetValue<string>().ShouldBe("Int64");
        parameters["PipelineName"]!["value"]!["value"]!.GetValue<string>().ShouldBe("@pipeline().Pipeline");
        Find("Log Recon Started")["typeProperties"]!["storedProcedureParameters"]!["Status"]!["value"]!.GetValue<string>().ShouldBe("Started");
    }

    [Fact]
    public void Failures_that_retrying_cannot_fix_are_not_retried_but_transient_ones_are()
    {
        AdfRetry.IsRetriable(Failure(typeof(ReconAlertRejectedException))).ShouldBeFalse();
        AdfRetry.IsRetriable(Failure(typeof(ReconAlertConfigurationException))).ShouldBeFalse();
        AdfRetry.IsRetriable(Failure(typeof(ReconInputException))).ShouldBeFalse();
        AdfRetry.IsRetriable(Failure(typeof(ReconAlertUnavailableException))).ShouldBeTrue();
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

    [GeneratedRegex(@"@\{(?<e>[^}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Interpolation();
}
