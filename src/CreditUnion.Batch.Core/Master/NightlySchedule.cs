using CreditUnion.Batch.Core.Pipelines;

namespace CreditUnion.Batch.Core.Master;

/// <summary>
/// TR_Nightly_0300_Pacific (<c>adf/trigger</c>): every day at 03:00 Pacific, passing the previous Pacific day as
/// <c>businessDate</c>. 03:00 lies outside the 01:00-02:59 hours that daylight saving skips or repeats, so it occurs exactly
/// once on every calendar day. The Functions host evaluates <see cref="Expression"/> in <c>WEBSITE_TIME_ZONE</c>; this class
/// works out which occurrence an invocation belongs to, because the isolated worker does not pass ADF's
/// <c>trigger().scheduledTime</c> (ADR 0016).
/// </summary>
public static class NightlySchedule
{
    /// <summary>The NCRONTAB expression of the timer function (second, minute, hour, day, month, weekday).</summary>
    public const string Expression = "0 0 3 * * *";

    /// <summary>The wall-clock time of each occurrence in <see cref="BusinessCalendar.Pacific"/>.</summary>
    public static TimeOnly LocalTime { get; } = new(3, 0);

    /// <summary>The occurrence on a Pacific calendar date, in UTC.</summary>
    public static DateTimeOffset OccurrenceOn(DateOnly localDate)
    {
        var zone = BusinessCalendar.Pacific;
        var local = localDate.ToDateTime(LocalTime);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
        {
            throw new InvalidOperationException($"{LocalTime:HH:mm} on {localDate:yyyy-MM-dd} is skipped or repeated in {zone.Id}; the schedule must avoid daylight saving hours.");
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>How early the host may fire and still belong to the coming occurrence (timer drift is milliseconds; this is generous).</summary>
    public static TimeSpan EarlyFireTolerance { get; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The occurrence an invocation at <paramref name="firedAt"/> belongs to (ADF's <c>trigger().scheduledTime</c>): the latest
    /// occurrence at or before the fire time, so a late fire, or a manual run of the timer function in the afternoon, processes
    /// the previous Pacific day and never the current, incomplete one.
    /// <list type="bullet">
    /// <item>On time (<paramref name="isPastDue"/> false): a fire up to <see cref="EarlyFireTolerance"/> before 03:00 belongs to that 03:00.</item>
    /// <item>Past due (the host is catching up a missed occurrence at start-up): no tolerance, the missed occurrence is in the past.</item>
    /// </list>
    /// </summary>
    public static DateTimeOffset ScheduledOccurrence(DateTimeOffset firedAt, bool isPastDue)
    {
        var at = isPastDue ? firedAt : firedAt + EarlyFireTolerance;
        var date = BusinessCalendar.PacificDateOf(at);
        return OccurrenceOn(date) <= at ? OccurrenceOn(date) : OccurrenceOn(date.AddDays(-1));
    }

    /// <summary>The trigger's <c>businessDate</c>: <c>formatDateTime(addDays(convertFromUtc(trigger().scheduledTime, 'Pacific Standard Time'), -1), 'yyyy-MM-dd')</c>.</summary>
    public static DateOnly BusinessDateFor(DateTimeOffset scheduledTime) => BusinessCalendar.PreviousPacificDay(scheduledTime);

    /// <summary>
    /// The input the trigger starts the master with. The ADF trigger passes only <c>businessDate</c>, so reconTolerance and
    /// retainUntil keep their defaults. <see cref="NightlyBatchInput.TriggerTimeUtc"/> is the scheduled occurrence.
    /// </summary>
    public static NightlyBatchInput StartInput(DateTimeOffset firedAt, bool isPastDue)
    {
        var scheduled = ScheduledOccurrence(firedAt, isPastDue);
        return new NightlyBatchInput { BusinessDate = BusinessDateFor(scheduled), TriggerName = TriggerNames.Nightly, TriggerTimeUtc = scheduled };
    }
}
