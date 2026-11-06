using CreditUnion.Batch.Core.Extract;

namespace CreditUnion.Batch.Core.Card;

/// <summary>
/// The activity policies of PL_IngestCardSettlement, copied from <c>adf/pipeline/PL_IngestCardSettlement.json</c>
/// (the source of truth); <c>CardPolicyTests</c> compares every value with that file. One entry per ADF
/// activity that has a policy; the two Log activities that call <c>ops.usp_LogBatchRunEvent</c> share one.
/// </summary>
public sealed record CardActivityPolicies(
    ActivityPolicy LogEvent,
    ActivityPolicy ResetStaging,
    ActivityPolicy ListLandingFiles,
    ActivityPolicy CopyToRawStaging,
    ActivityPolicy Validate,
    ActivityPolicy Promote,
    ActivityPolicy Archive,
    ActivityPolicy DeleteArchived,
    ActivityPolicy LogArchived,
    ActivityPolicy WriteManifest,
    ActivityPolicy Quarantine,
    ActivityPolicy DeleteQuarantined,
    ActivityPolicy LogQuarantined,
    ActivityPolicy Merge)
{
    /// <summary>"For Each Settlement File": <c>batchCount</c>.</summary>
    public const int BatchCount = 4;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    public static CardActivityPolicies Adf { get; } = new(
        LogEvent: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(5)),
        ResetStaging: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(5)),
        ListLandingFiles: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(5)),
        CopyToRawStaging: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(30)),
        Validate: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(10)),
        Promote: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(10)),
        Archive: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(10)),
        DeleteArchived: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(5)),
        LogArchived: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(5)),
        WriteManifest: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(10)),
        Quarantine: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(10)),
        DeleteQuarantined: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(5)),
        LogQuarantined: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(5)),
        Merge: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(30)));
}
