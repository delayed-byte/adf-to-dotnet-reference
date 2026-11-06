using System.Reflection;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using NCrontab;

namespace CreditUnion.Batch.TestHarness.Orchestration;

/// <summary>
/// Computes when the Functions host would fire a timer-triggered function: the NCRONTAB expression of
/// its <see cref="TimerTriggerAttribute"/> (a <c>%Setting%</c> is resolved from the app settings) is
/// evaluated in the wall-clock time of <c>WEBSITE_TIME_ZONE</c> (UTC when unset), as the host does,
/// with NCrontab.Signed, the library the host uses. AT-18 uses it to drive the trigger across the
/// daylight saving changes.
/// <para>
/// Local times that do not exist (the skipped spring-forward hour) are not fired and repeated local
/// times (the fall-back hour) fire once, at the first occurrence. The host's behaviour inside those
/// two hours is not documented precisely; a schedule that avoids 01:00-02:59 local, like 03:00, is
/// unaffected (ADR 0008).
/// </para>
/// </summary>
public sealed class TimerTriggerEmulator
{
    public const string TimeZoneSetting = "WEBSITE_TIME_ZONE";

    private readonly CrontabSchedule schedule;

    public TimerTriggerEmulator(string expression, TimeZoneInfo timeZone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        if (expression.Contains(':', StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Timer schedule '{expression}' is a TimeSpan; only NCRONTAB expressions are emulated.");
        }

        Expression = expression;
        TimeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        schedule = CrontabSchedule.Parse(expression, new CrontabSchedule.ParseOptions { IncludingSeconds = fields == 6 });
    }

    public string Expression { get; }

    public TimeZoneInfo TimeZone { get; }

    /// <summary>The schedule of a timer function, with app settings as <c>func start</c> or Azure would supply them.</summary>
    public static TimerTriggerEmulator For(FunctionEntry function, IReadOnlyDictionary<string, string> appSettings)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(appSettings);
        var attribute = function.Method.GetParameters().Select(p => p.GetCustomAttribute<TimerTriggerAttribute>()).Single(a => a is not null)!;
        var expression = attribute.Schedule;
        if (expression.Length > 2 && expression[0] == '%' && expression[^1] == '%')
        {
            var setting = expression[1..^1];
            expression = appSettings.TryGetValue(setting, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new InvalidOperationException($"Timer function {function.Name} uses the app setting '{setting}', which is not defined.");
        }

        var zone = appSettings.TryGetValue(TimeZoneSetting, out var zoneId) && !string.IsNullOrWhiteSpace(zoneId)
            ? TimeZoneInfo.FindSystemTimeZoneById(zoneId)
            : TimeZoneInfo.Utc;
        return new TimerTriggerEmulator(expression, zone);
    }

    /// <summary>The <c>Values</c> of a local.settings.json file (the app settings of <c>func start</c>).</summary>
    public static IReadOnlyDictionary<string, string> ReadLocalSettings(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty("Values", out var values)
            ? values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Fire times in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), in UTC.</summary>
    public IReadOnlyList<DateTimeOffset> Occurrences(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var localFrom = TimeZoneInfo.ConvertTime(fromUtc, TimeZone).DateTime.AddDays(-1);
        var localTo = TimeZoneInfo.ConvertTime(toUtc, TimeZone).DateTime.AddDays(1);
        var result = new List<DateTimeOffset>();
        foreach (var local in schedule.GetNextOccurrences(localFrom, localTo))
        {
            if (TimeZone.IsInvalidTime(local))
            {
                continue;
            }

            var offset = TimeZone.IsAmbiguousTime(local) ? TimeZone.GetAmbiguousTimeOffsets(local).Max() : TimeZone.GetUtcOffset(local);
            var utc = new DateTimeOffset(local, offset).ToUniversalTime();
            if (utc >= fromUtc && utc < toUtc)
            {
                result.Add(utc);
            }
        }

        return result;
    }
}
