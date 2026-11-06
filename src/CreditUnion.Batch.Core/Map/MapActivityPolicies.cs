using CreditUnion.Batch.Core.Extract;

namespace CreditUnion.Batch.Core.Map;

/// <summary>
/// The activity policies of PL_MapAndAggregate, copied from <c>adf/pipeline/PL_MapAndAggregate.json</c> (the source of
/// truth); <c>MapPolicyTests</c> compares every value with that file. One entry per ADF activity that has a policy;
/// "Log Map Started" and "Log Map Succeeded" share <see cref="LogEvent"/>.
/// </summary>
public sealed record MapActivityPolicies(
    ActivityPolicy LogEvent,
    ActivityPolicy GetGlMapping,
    ActivityPolicy CacheGlMapping,
    ActivityPolicy LookupUnmapped,
    ActivityPolicy LogUnmapped,
    ActivityPolicy Aggregate)
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    public static MapActivityPolicies Adf { get; } = new(
        LogEvent: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(5)),
        GetGlMapping: new ActivityPolicy(3, Interval, TimeSpan.FromMinutes(5)),
        CacheGlMapping: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(5)),
        LookupUnmapped: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(5)),
        LogUnmapped: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(5)),
        Aggregate: new ActivityPolicy(1, TimeSpan.FromSeconds(60), TimeSpan.FromHours(2)));
}

/// <summary>
/// The fixed parts of the Web activity "Get GL Mapping From KeyBridge" (ADR 0012). The base URL and the password
/// come from configuration (<c>KeyBridgeOptions</c>); <c>MapPolicyTests</c> compares these values with the ADF JSON.
/// </summary>
public static class KeyBridgeWebActivity
{
    /// <summary>Appended to <c>keyBridgeBaseUrl</c> by <c>@concat(...)</c>.</summary>
    public const string MappingsPath = "/v1/reference/transaction-codes/gl-mappings";

    /// <summary><c>authentication.username</c>, the default of <c>KeyBridgeOptions.Username</c>.</summary>
    public const string Username = "cu-batch";

    /// <summary><c>headers.X-Correlation-Id</c>: the batch run ID.</summary>
    public const string CorrelationHeader = "X-Correlation-Id";

    /// <summary>ADF's Web activity output limit (4 MB). A larger response fails without retrying.</summary>
    public const int MaxResponseBytes = 4 * 1024 * 1024;

    /// <summary><c>httpRequestTimeout</c>: how long one request may wait for the response. The activity <c>timeout</c> (5 min) bounds the attempt.</summary>
    public static TimeSpan HttpRequestTimeout { get; } = TimeSpan.FromMinutes(1);
}
