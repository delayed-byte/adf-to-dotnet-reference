using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using CreditUnion.Batch.Core.Master;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.Functions.Triggers;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Orchestration;
using CreditUnion.Batch.TestHarness.Scenarios;
using Microsoft.Azure.Functions.Worker;

namespace CreditUnion.Batch.UnitTests.Master;

/// <summary>
/// The business-date and schedule rules of PL_Master_NightlyBatch and TR_Nightly_0300_Pacific (ADR 0016), hand-checked across
/// both 2026 daylight saving changes (spring forward 2026-03-08 02:00 PST, fall back 2026-11-01 02:00 PDT) and at midnight.
/// </summary>
public sealed class BusinessDateAndScheduleTests
{
    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static DateOnly Date(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("2026-11-05T07:59:59Z", "2026-11-03", "23:59:59 PST on Nov 4: the previous day is Nov 3")]
    [InlineData("2026-11-05T08:00:00Z", "2026-11-04", "00:00 PST on Nov 5")]
    [InlineData("2026-11-05T11:00:00Z", "2026-11-04", "03:00 PST on Nov 5, the nightly run")]
    [InlineData("2026-03-08T07:59:59Z", "2026-03-06", "23:59:59 PST on Mar 7")]
    [InlineData("2026-03-08T08:00:00Z", "2026-03-07", "00:00 PST on Mar 8, the spring-forward day")]
    [InlineData("2026-03-08T09:59:59Z", "2026-03-07", "01:59:59 PST, the last second before the skipped hour")]
    [InlineData("2026-03-08T10:00:00Z", "2026-03-07", "03:00 PDT on Mar 8 (02:00-02:59 does not exist)")]
    [InlineData("2026-03-09T06:59:59Z", "2026-03-07", "23:59:59 PDT on Mar 8, the end of a 23-hour day")]
    [InlineData("2026-03-09T07:00:00Z", "2026-03-08", "00:00 PDT on Mar 9")]
    [InlineData("2026-11-01T07:00:00Z", "2026-10-31", "00:00 PDT on Nov 1, the fall-back day")]
    [InlineData("2026-11-01T08:30:00Z", "2026-10-31", "01:30 PDT, the first of the repeated hour")]
    [InlineData("2026-11-01T09:30:00Z", "2026-10-31", "01:30 PST, the second of the repeated hour")]
    [InlineData("2026-11-01T11:00:00Z", "2026-10-31", "03:00 PST on Nov 1")]
    [InlineData("2026-11-02T07:59:59Z", "2026-10-31", "23:59:59 PST on Nov 1, the end of a 25-hour day")]
    [InlineData("2026-11-02T08:00:00Z", "2026-11-01", "00:00 PST on Nov 2")]
    public void The_previous_Pacific_day_is_the_local_date_minus_one_calendar_day(string instantUtc, string expected, string because)
    {
        BusinessCalendar.PreviousPacificDay(Utc(instantUtc)).ShouldBe(Date(expected), because);
    }

    [Fact]
    public void Resolve_Business_Date_uses_the_override_and_otherwise_the_previous_Pacific_day_of_the_trigger_time()
    {
        BusinessCalendar.Resolve(new DateOnly(2026, 11, 3), Utc("2026-11-05T11:00:00Z")).ShouldBe(new DateOnly(2026, 11, 3), "a rerun overrides businessDate");
        BusinessCalendar.Resolve(null, Utc("2026-11-05T11:00:00Z")).ShouldBe(new DateOnly(2026, 11, 4), "empty businessDate: the previous Pacific day of TriggerTime");
        BusinessCalendar.Resolve(null, Utc("2026-11-05T07:30:00Z")).ShouldBe(new DateOnly(2026, 11, 3), "a manual run at 23:30 PST on Nov 4 processes Nov 3, as the ADF expression would");
        BusinessCalendar.TimeZoneId.ShouldBe("America/Los_Angeles");
    }

    [Theory]
    [InlineData("2026-03-07", "2026-03-07T11:00:00Z")]
    [InlineData("2026-03-08", "2026-03-08T10:00:00Z")]
    [InlineData("2026-03-09", "2026-03-09T10:00:00Z")]
    [InlineData("2026-10-31", "2026-10-31T10:00:00Z")]
    [InlineData("2026-11-01", "2026-11-01T11:00:00Z")]
    [InlineData("2026-11-02", "2026-11-02T11:00:00Z")]
    public void The_occurrence_is_03_00_Pacific_on_every_day_including_both_DST_change_days(string localDate, string expectedUtc)
    {
        NightlySchedule.OccurrenceOn(Date(localDate)).ShouldBe(Utc(expectedUtc));
    }

    [Fact]
    public void An_on_time_fire_a_little_early_or_late_belongs_to_its_own_occurrence()
    {
        var occurrence = Utc("2026-03-08T10:00:00Z");

        NightlySchedule.ScheduledOccurrence(occurrence.AddSeconds(1.5), isPastDue: false).ShouldBe(occurrence, "the host fires just after the occurrence");
        NightlySchedule.ScheduledOccurrence(occurrence.AddMilliseconds(-200), isPastDue: false).ShouldBe(occurrence, "a timer that fires a little early still belongs to today's run, not yesterday's");
        NightlySchedule.ScheduledOccurrence(occurrence.AddHours(6), isPastDue: false).ShouldBe(occurrence);
        NightlySchedule.ScheduledOccurrence(occurrence - NightlySchedule.EarlyFireTolerance, isPastDue: false).ShouldBe(occurrence);
        NightlySchedule.ScheduledOccurrence(occurrence - NightlySchedule.EarlyFireTolerance - TimeSpan.FromSeconds(1), isPastDue: false)
            .ShouldBe(occurrence.AddDays(-1).AddHours(1), "more than five minutes early is the previous day's occurrence (03:00 PST on Mar 7 = 11:00Z)");
        NightlySchedule.StartInput(occurrence.AddMilliseconds(-200), isPastDue: false).BusinessDate.ShouldBe(new DateOnly(2026, 3, 7));
    }

    [Fact]
    public void Running_the_timer_function_by_hand_in_the_afternoon_processes_yesterday_never_the_incomplete_current_day()
    {
        var afternoon = Utc("2026-11-06T23:15:00Z"); // 15:15 PST on Nov 6

        NightlySchedule.ScheduledOccurrence(afternoon, isPastDue: false).ShouldBe(Utc("2026-11-06T11:00:00Z"));
        NightlySchedule.StartInput(afternoon, isPastDue: false).BusinessDate.ShouldBe(new DateOnly(2026, 11, 5));
        NightlySchedule.StartInput(Utc("2026-11-07T07:59:00Z"), isPastDue: false).BusinessDate.ShouldBe(new DateOnly(2026, 11, 5), "23:59 PST, still before the next 03:00");
    }

    [Fact]
    public void A_past_due_fire_runs_the_latest_missed_occurrence_even_many_hours_late()
    {
        // Host down from 02:00 PST on Nov 5 until 01:00 PST on Nov 6: the host runs the missed Nov 5 occurrence once at start-up.
        var startUp = Utc("2026-11-06T09:00:00Z");

        NightlySchedule.ScheduledOccurrence(startUp, isPastDue: true).ShouldBe(Utc("2026-11-05T11:00:00Z"));
        NightlySchedule.StartInput(startUp, isPastDue: true).BusinessDate.ShouldBe(new DateOnly(2026, 11, 4), "the missed run's date, not the day after");
        NightlySchedule.StartInput(Utc("2026-11-06T11:00:01Z"), isPastDue: false).BusinessDate.ShouldBe(new DateOnly(2026, 11, 5), "the next regular run processes the next date");

        // Back up at 02:58 PST on Nov 6, two minutes before the next occurrence: a past-due fire still means the missed Nov 5 one.
        NightlySchedule.StartInput(Utc("2026-11-06T10:58:00Z"), isPastDue: true).BusinessDate.ShouldBe(new DateOnly(2026, 11, 4));
    }

    [Fact]
    public void The_start_input_carries_the_business_date_the_trigger_name_and_the_scheduled_time_with_the_ADF_defaults()
    {
        var input = NightlySchedule.StartInput(Utc("2026-11-01T11:00:01.5Z"), isPastDue: false);

        input.ShouldBe(new NightlyBatchInput
        {
            BusinessDate = new DateOnly(2026, 10, 31),
            TriggerName = TriggerNames.Nightly,
            TriggerTimeUtc = Utc("2026-11-01T11:00:00Z"),
        });
        input.ReconTolerance.ShouldBe(0.00m, "the ADF trigger passes only businessDate");
        input.RetainUntil.ShouldBe("TBD");
    }

    [Fact]
    public void The_golden_DST_schedule_is_reproduced_day_by_day()
    {
        var schedule = Scenario.Load("dst-transitions").Golden.Schedule.ShouldNotBeNull();

        foreach (var day in schedule)
        {
            var occurrence = NightlySchedule.OccurrenceOn(Date(day.LocalDate));
            occurrence.ShouldBe(Utc(day.FireTimeUtc), day.LocalDate);
            NightlySchedule.StartInput(occurrence.AddSeconds(1.5), isPastDue: false).BusinessDate.ShouldBe(Date(day.BusinessDate), day.LocalDate);
        }
    }

    [Fact]
    public void The_timer_attribute_is_the_ADF_trigger_recurrence_and_never_runs_on_start_up()
    {
        var method = typeof(NightlyBatchTrigger).GetMethod(nameof(NightlyBatchTrigger.RunAsync))!;
        var timer = method.GetParameters().Select(p => p.GetCustomAttribute<TimerTriggerAttribute>()).Single(a => a is not null)!;
        var trigger = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Adf, "trigger", TriggerNames.Nightly + ".json")))!["properties"]!;
        var recurrence = trigger["typeProperties"]!["recurrence"]!;

        timer.Schedule.ShouldBe(NightlySchedule.Expression);
        timer.RunOnStartup.ShouldBeFalse("a start-up run would start a second master for a date already processed");
        timer.UseMonitor.ShouldBeTrue("the schedule monitor is what detects a missed occurrence (IsPastDue)");
        trigger["type"]!.GetValue<string>().ShouldBe("ScheduleTrigger");
        (recurrence["frequency"]!.GetValue<string>(), recurrence["interval"]!.GetValue<int>()).ShouldBe(("Day", 1));
        recurrence["timeZone"]!.GetValue<string>().ShouldBe("Pacific Standard Time", "ADF's DST-aware Windows ID for America/Los_Angeles");
        recurrence["schedule"]!["hours"]!.AsArray().Select(h => h!.GetValue<int>()).ShouldBe([NightlySchedule.LocalTime.Hour]);
        recurrence["schedule"]!["minutes"]!.AsArray().Select(m => m!.GetValue<int>()).ShouldBe([NightlySchedule.LocalTime.Minute]);
        TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time").HasSameRules(BusinessCalendar.Pacific).ShouldBeTrue();

        var emulator = TimerTriggerEmulator.For(FunctionCatalog.FromAssembly(typeof(NightlyBatchTrigger).Assembly).Timer(TriggerNames.Nightly),
            TimerTriggerEmulator.ReadLocalSettings(RepositoryPaths.FunctionsLocalSettings));
        emulator.TimeZone.HasSameRules(BusinessCalendar.Pacific).ShouldBeTrue("local.settings.json sets WEBSITE_TIME_ZONE to Pacific Time");
    }

    [Fact]
    public void The_trigger_passes_only_businessDate_with_the_previous_Pacific_day_of_its_scheduled_time()
    {
        var trigger = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Adf, "trigger", TriggerNames.Nightly + ".json")))!["properties"]!;
        var pipeline = trigger["pipelines"]!.AsArray().ShouldHaveSingleItem()!;

        pipeline["pipelineReference"]!["referenceName"]!.GetValue<string>().ShouldBe(PipelineNames.Master);
        pipeline["parameters"]!.AsObject().Select(p => p.Key).ShouldBe(["businessDate"]);
        pipeline["parameters"]!["businessDate"]!.GetValue<string>()
            .ShouldBe("@formatDateTime(addDays(convertFromUtc(trigger().scheduledTime, 'Pacific Standard Time'), -1), 'yyyy-MM-dd')");
    }
}
