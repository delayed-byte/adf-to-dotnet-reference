namespace CreditUnion.Batch.Core.Master;

/// <summary>
/// The business date of the nightly batch (PRD): a <see cref="DateOnly"/> in Pacific Time (<c>America/Los_Angeles</c>,
/// ADF's <c>Pacific Standard Time</c>, which follows daylight saving). Pure functions, so the orchestrator may call them.
/// </summary>
public static class BusinessCalendar
{
    /// <summary>The IANA ID of the business time zone. Windows resolves it through ICU (.NET 6 and later).</summary>
    public const string TimeZoneId = "America/Los_Angeles";

    public static TimeZoneInfo Pacific { get; } = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    /// <summary>The Pacific calendar date on which <paramref name="instant"/> falls.</summary>
    public static DateOnly PacificDateOf(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Pacific).DateTime);

    /// <summary>
    /// ADF: <c>formatDateTime(addDays(convertFromUtc(t, 'Pacific Standard Time'), -1), 'yyyy-MM-dd')</c>. ADF subtracts a
    /// day from the local wall-clock time and keeps the date, which is the local date minus one calendar day; it never
    /// subtracts 24 elapsed hours, so a 23- or 25-hour day changes nothing.
    /// </summary>
    public static DateOnly PreviousPacificDay(DateTimeOffset instant) => PacificDateOf(instant).AddDays(-1);

    /// <summary>
    /// Resolve Business Date: <c>@if(empty(pipeline().parameters.businessDate), formatDateTime(addDays(convertFromUtc(
    /// pipeline().TriggerTime, 'Pacific Standard Time'), -1), 'yyyy-MM-dd'), pipeline().parameters.businessDate)</c>.
    /// </summary>
    public static DateOnly Resolve(DateOnly? businessDate, DateTimeOffset triggerTime) => businessDate ?? PreviousPacificDay(triggerTime);
}
