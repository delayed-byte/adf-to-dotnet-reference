using CreditUnion.Batch.Core.Extract;

namespace CreditUnion.Batch.Core.Reconciliation;

/// <summary>
/// The activity policies of PL_Reconciliation, copied from <c>adf/pipeline/PL_Reconciliation.json</c> (the source of
/// truth); <c>ReconPolicyTests</c> compares every value with that file. One entry per ADF activity that the .NET
/// version runs as an activity. "Log Recon Started" and "Log Recon Outcome" share <see cref="LogEvent"/>. The Web
/// activity "Get Alert Webhook Url" has no entry: it is configuration here (ADR 0013).
/// </summary>
public sealed record ReconActivityPolicies(
    ActivityPolicy LogEvent,
    ActivityPolicy CardTotals,
    ActivityPolicy GlTotals,
    ActivityPolicy RecordExceptions,
    ActivityPolicy PostAlert,
    ActivityPolicy MarkAlertSent)
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    public static ReconActivityPolicies Adf { get; } = new(
        LogEvent: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(5)),
        CardTotals: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(5)),
        GlTotals: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(5)),
        RecordExceptions: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(5)),
        PostAlert: new ActivityPolicy(3, Interval, TimeSpan.FromMinutes(2)),
        MarkAlertSent: new ActivityPolicy(2, Interval, TimeSpan.FromMinutes(5)));
}

/// <summary>
/// The fixed parts of the Web activities of PL_Reconciliation (ADR 0013). The webhook URL itself is configuration
/// (<c>ReconAlertWebhookOptions</c>); <c>ReconPolicyTests</c> compares these values with the ADF JSON.
/// </summary>
public static class ReconAlertWebActivity
{
    /// <summary>"Post Recon Alert": <c>headers.Content-Type</c>.</summary>
    public const string ContentType = "application/json";

    /// <summary>The Key Vault secret "Get Alert Webhook Url" reads; in Azure it backs the <c>ReconAlertWebhook:Url</c> setting.</summary>
    public const string KeyVaultSecretName = "recon-alert-webhook-url";

    /// <summary>The <c>type</c> property of the alert body.</summary>
    public const string AlertType = "ReconciliationException";

    /// <summary>"Post Recon Alert" <c>httpRequestTimeout</c>: how long one request may wait for the response. The activity <c>timeout</c> (2 min) bounds the attempt.</summary>
    public static TimeSpan HttpRequestTimeout { get; } = TimeSpan.FromMinutes(1);
}
