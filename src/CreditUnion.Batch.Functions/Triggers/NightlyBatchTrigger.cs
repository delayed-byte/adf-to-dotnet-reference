using System.Globalization;
using CreditUnion.Batch.Core.Master;
using CreditUnion.Batch.Core.Pipelines;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreditUnion.Batch.Functions.Triggers;

/// <summary>
/// TR_Nightly_0300_Pacific: starts PL_Master_NightlyBatch at 03:00 Pacific every day with businessDate set to the previous
/// Pacific day of the scheduled occurrence (ADR 0008, ADR 0016). The NCRONTAB schedule is evaluated in WEBSITE_TIME_ZONE
/// (local.settings.json locally, an app setting in Azure). The fire time comes from the injected <see cref="TimeProvider"/>,
/// never from the wall clock, and each invocation starts exactly one master under a new GUID, its batchRunId.
/// <para>
/// A past-due invocation (the host was down at 03:00 and catches up once at start-up) runs the latest missed occurrence; earlier
/// missed days are not back-filled, as with an ADF schedule trigger. A second invocation for the same occurrence starts a second
/// master, which every pipeline tolerates as a rerun (ADR 0016).
/// </para>
/// </summary>
public static partial class NightlyBatchTrigger
{
    [Function(TriggerNames.Nightly)]
    public static async Task RunAsync([TimerTrigger("0 0 3 * * *")] TimerInfo timer, [DurableClient] DurableTaskClient client, FunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timer);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(context);
        var firedAt = context.InstanceServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var logger = context.InstanceServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(NightlyBatchTrigger).FullName!);

        var input = NightlySchedule.StartInput(firedAt, timer.IsPastDue);
        var scheduled = input.TriggerTimeUtc!.Value;
        if (timer.IsPastDue)
        {
            logger.LogPastDue(scheduled, firedAt);
        }

        var instanceId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        await client.ScheduleNewOrchestrationInstanceAsync(PipelineNames.Master, input, new StartOrchestrationOptions(instanceId), cancellationToken);
        logger.LogMasterStarted(instanceId, input.BusinessDate!.Value, scheduled, firedAt);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Started PL_Master_NightlyBatch {BatchRunId} for business date {BusinessDate:yyyy-MM-dd} (occurrence {ScheduledUtc:u}, fired {FiredUtc:u}).")]
    private static partial void LogMasterStarted(this ILogger logger, string batchRunId, DateOnly businessDate, DateTimeOffset scheduledUtc, DateTimeOffset firedUtc);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TR_Nightly_0300_Pacific is past due: running the missed occurrence {ScheduledUtc:u} at {FiredUtc:u}. Earlier missed days, if any, need a manual run.")]
    private static partial void LogPastDue(this ILogger logger, DateTimeOffset scheduledUtc, DateTimeOffset firedUtc);
}
