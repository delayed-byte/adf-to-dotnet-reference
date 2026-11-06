using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.DurableTask;

namespace CreditUnion.Batch.TestHarness.Adf;

/// <summary>
/// The retry policy of one ADF activity, read from adf/pipeline (the source of truth). ADF's
/// <c>retry</c> counts retries after the first attempt and waits a fixed <c>retryIntervalInSeconds</c>
/// between them, so the equivalent Durable <see cref="RetryPolicy"/> has <c>retry + 1</c> attempts,
/// that first interval and a backoff coefficient of 1.
/// </summary>
public sealed record AdfActivityPolicy(string Pipeline, string Activity, int Retry, TimeSpan RetryInterval, TimeSpan Timeout)
{
    private static readonly string[] NestedActivityLists = ["activities", "ifTrueActivities", "ifFalseActivities", "defaultActivities"];

    public int MaxNumberOfAttempts => Retry + 1;

    public static AdfActivityPolicy Read(string pipeline, string activity)
    {
        var path = Path.Combine(RepositoryPaths.Adf, "pipeline", pipeline + ".json");
        var root = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException($"{path} is empty.");
        var found = Find(root["properties"]?["activities"] as JsonArray, activity)
            ?? throw new InvalidOperationException($"{pipeline} has no activity named '{activity}'.");
        var policy = found["policy"] as JsonObject ?? throw new InvalidOperationException($"{pipeline}/{activity} has no policy.");
        return new AdfActivityPolicy(pipeline, activity,
            policy["retry"]?.GetValue<int>() ?? 0,
            TimeSpan.FromSeconds(policy["retryIntervalInSeconds"]?.GetValue<int>() ?? 30),
            TimeSpan.Parse(policy["timeout"]?.GetValue<string>() ?? "0.12:00:00", CultureInfo.InvariantCulture));
    }

    /// <summary>Whether a Durable retry policy reproduces this ADF policy (attempts and fixed interval).</summary>
    public bool IsImplementedBy(RetryPolicy? policy) =>
        policy is not null
        && policy.MaxNumberOfAttempts == MaxNumberOfAttempts
        && policy.FirstRetryInterval == RetryInterval
        && Math.Abs(policy.BackoffCoefficient - 1.0) < 1e-9
        && policy.MaxRetryInterval >= RetryInterval;

    public static string Describe(RetryPolicy? policy) => policy is null
        ? "no retry policy"
        : string.Create(CultureInfo.InvariantCulture,
            $"{policy.MaxNumberOfAttempts} attempts, first interval {policy.FirstRetryInterval}, backoff {policy.BackoffCoefficient}, max interval {policy.MaxRetryInterval}");

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Pipeline}/{Activity}: retry {Retry} every {RetryInterval.TotalSeconds:0} s ({MaxNumberOfAttempts} attempts), timeout {Timeout}");

    private static JsonObject? Find(JsonArray? activities, string name)
    {
        foreach (var node in activities?.OfType<JsonObject>() ?? [])
        {
            if (string.Equals(node["name"]?.GetValue<string>(), name, StringComparison.Ordinal))
            {
                return node;
            }

            if (node["typeProperties"] is not JsonObject properties)
            {
                continue;
            }

            foreach (var list in NestedActivityLists)
            {
                if (Find(properties[list] as JsonArray, name) is { } nested)
                {
                    return nested;
                }
            }

            foreach (var branch in (properties["cases"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                if (Find(branch["activities"] as JsonArray, name) is { } nested)
                {
                    return nested;
                }
            }
        }

        return null;
    }
}
