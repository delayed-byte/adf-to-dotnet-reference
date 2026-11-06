namespace CreditUnion.Batch.Core.Extract;

/// <summary>
/// The policy block of one ADF activity: <c>retry</c> counts retries after the first attempt, the wait
/// between them is fixed, and <c>timeout</c> bounds one attempt (ADR 0008).
/// </summary>
public sealed record ActivityPolicy(int Retry, TimeSpan RetryInterval, TimeSpan Timeout)
{
    /// <summary><c>retry + 1</c>: the first attempt plus the retries.</summary>
    public int MaxNumberOfAttempts => Retry + 1;
}

/// <summary>
/// The activity policies of PL_ExtractKeyStone, copied from <c>adf/pipeline/PL_ExtractKeyStone.json</c>
/// (the source of truth). <c>ExtractActivityPolicyTests</c> compares every value with that file, so an
/// edit to the ADF JSON that is not mirrored here fails the build.
/// </summary>
public sealed record ExtractActivityPolicies(
    ActivityPolicy LogEvent,
    ActivityPolicy LookupConfig,
    ActivityPolicy CopyToStaging,
    ActivityPolicy Merge)
{
    /// <summary>"For Each Source Table": <c>batchCount</c>.</summary>
    public const int BatchCount = 3;

    public static ExtractActivityPolicies Adf { get; } = new(
        LogEvent: new ActivityPolicy(2, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)),
        LookupConfig: new ActivityPolicy(2, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)),
        CopyToStaging: new ActivityPolicy(3, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1)),
        Merge: new ActivityPolicy(2, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30)));
}
