using CreditUnion.Batch.Core.Pipelines;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;

namespace CreditUnion.Batch.Functions.Triggers;

/// <summary>
/// TR_Nightly_0300_Pacific: starts PL_Master_NightlyBatch at 03:00 Pacific every day with businessDate
/// set to the previous Pacific day (ADR 0008). The NCRONTAB schedule is evaluated in WEBSITE_TIME_ZONE
/// (local.settings.json locally, an app setting in Azure). AT-18 drives this function through a timer
/// emulator; the body is converted in Phase 8.
/// </summary>
public static class NightlyBatchTrigger
{
    [Function(TriggerNames.Nightly)]
    public static Task RunAsync([TimerTrigger("0 0 3 * * *")] TimerInfo timer, [DurableClient] DurableTaskClient client, CancellationToken cancellationToken) =>
        throw new NotImplementedException($"{TriggerNames.Nightly} is not converted yet (docs/PRD.md, Phase 8).");
}
