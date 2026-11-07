using CreditUnion.Batch.Core.Extract;

namespace CreditUnion.Batch.Core.Master;

/// <summary>
/// The activity policies of PL_Master_NightlyBatch, copied from <c>adf/pipeline/PL_Master_NightlyBatch.json</c> (the source of
/// truth); <c>MasterPolicyTests</c> compares every value with that file. "Log Batch Started", "Log Batch Succeeded" and the five
/// "Log Failed At …" activities share <see cref="LogEvent"/>. The four Execute Pipeline activities have no policy in ADF, so the
/// sub-orchestration calls have no <c>RetryPolicy</c>; Resolve Business Date (Set Variable) and the Fail activities are code (ADR 0014).
/// </summary>
public sealed record MasterActivityPolicies(ActivityPolicy LogEvent, ActivityPolicy ExportEvidence)
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    public static MasterActivityPolicies Adf { get; } = new(
        LogEvent: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(5)),
        ExportEvidence: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(10)));

    /// <summary>"Export Run Evidence" source <c>queryTimeout</c>. It equals the activity timeout, which bounds the whole attempt.</summary>
    public static TimeSpan EvidenceQueryTimeout { get; } = TimeSpan.FromMinutes(10);
}
