using System.Diagnostics;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Orchestration;
using CreditUnion.Batch.TestHarness.Scenarios;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.DependencyInjection;

namespace CreditUnion.Batch.UnitTests.Harness;

/// <summary>
/// Self-tests of the in-process Durable host the acceptance tests run on (ADR 0008): JSON boundaries,
/// retry policies on virtual time, parallel versus sequential sub-orchestrations, failure propagation,
/// the thread rule, the recording client and the timer emulator.
/// </summary>
public sealed class InProcessFunctionHostTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 11, 0, 0, TimeSpan.Zero);

    private readonly FlakyDependency flaky = new();
    private readonly VirtualClock clock = new(Start);
    private readonly InProcessFunctionHost host;

    public InProcessFunctionHostTests()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(flaky)
            .AddSingleton<TimeProvider>(clock)
            .BuildServiceProvider();
        host = new InProcessFunctionHost(FunctionCatalog.FromAssembly(typeof(SampleActivities).Assembly), services, clock);
    }

    [Fact]
    public async Task Inputs_and_outputs_cross_every_boundary_as_json_and_activities_are_created_with_their_services()
    {
        var result = await host.RunOrchestrationAsync("EchoTwice", new SamplePayload(new DateOnly(2026, 11, 4), 1.10m, "hi"));

        result.Succeeded.ShouldBeTrue(result.Describe());
        var output = host.Converter.Deserialize<SamplePayload>(result.SerializedOutput)!;
        output.ShouldBe(new SamplePayload(new DateOnly(2026, 11, 4), 1.10m, "hi!!"));
        ((int)output.Amount.Scale).ShouldBe(2, "decimal scale survives the JSON round trip");
        host.Trace.Attempts.Select(a => a.Name).ShouldBe(["Echo", "Echo"]);
    }

    [Fact]
    public async Task A_retry_policy_retries_failed_attempts_after_its_interval_on_virtual_time()
    {
        flaky.FailuresBeforeSuccess = 2;
        var wall = Stopwatch.StartNew();

        var result = await host.RunOrchestrationAsync("RetryFlaky", "k1");

        result.Succeeded.ShouldBeTrue(result.Describe());
        var attempts = host.Trace.Attempts;
        attempts.Select(a => (a.Attempt, a.Failed)).ShouldBe([(1, true), (2, true), (3, false)]);
        attempts.ShouldAllBe(a => a.CallId == attempts[0].CallId);
        attempts[0].Failure!.ErrorType.ShouldBe(typeof(TimeoutException).FullName);
        attempts[0].Policy!.MaxNumberOfAttempts.ShouldBe(4);
        host.Trace.RetryDecisions.Select(r => r.Delay).ShouldBe([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)]);
        (clock.GetUtcNow() - Start).ShouldBe(TimeSpan.FromSeconds(60));
        (attempts[2].ScheduledAtUtc - attempts[0].ScheduledAtUtc).ShouldBe(TimeSpan.FromSeconds(60));
        wall.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "durable timers do not wait in real time");
    }

    [Fact]
    public async Task Retries_stop_after_the_last_attempt_and_the_failure_surfaces_as_TaskFailedException()
    {
        flaky.FailuresBeforeSuccess = 100;

        var result = await host.RunOrchestrationAsync("RetryFlaky", "k2");

        result.Succeeded.ShouldBeFalse();
        host.Trace.Attempts.Count.ShouldBe(4);
        host.Trace.RetryDecisions[^1].Delay.ShouldBeNull();
        result.Failure!.ErrorType.ShouldBe(typeof(TaskFailedException).FullName);
        result.Failure.InnerFailure!.ErrorType.ShouldBe(typeof(TimeoutException).FullName);
    }

    [Fact]
    public async Task A_failure_the_policy_does_not_handle_is_not_retried()
    {
        var result = await host.RunOrchestrationAsync("RetryOnlyTransient", null);

        result.Succeeded.ShouldBeFalse();
        host.Trace.Attempts.Count.ShouldBe(1, result.Describe());
        host.Trace.RetryDecisions.ShouldHaveSingleItem(result.Describe()).Delay.ShouldBeNull();
        result.Failure!.InnerFailure!.ErrorType.ShouldBe(typeof(InvalidDataException).FullName);
    }

    [Fact]
    public async Task Sub_orchestrations_awaited_together_run_in_parallel_and_later_ones_wait()
    {
        var result = await host.RunOrchestrationAsync("FanOut", null, "parent-1");

        result.Succeeded.ShouldBeTrue(result.Describe());
        var children = host.Trace.SubOrchestrationsOf("parent-1");
        children.Count.ShouldBe(3);
        var (a, b, c) = (children[0], children[1], children[2]);
        Math.Max(a.StartedSequence, b.StartedSequence).ShouldBeLessThan(Math.Min(a.CompletedSequence, b.CompletedSequence), "a and b overlap");
        c.ScheduledSequence.ShouldBeGreaterThan(Math.Max(a.CompletedSequence, b.CompletedSequence), "c starts after both");
        host.Trace.InstanceTree("parent-1").Count.ShouldBe(4);
    }

    [Fact]
    public async Task Sub_orchestrations_awaited_one_at_a_time_do_not_overlap()
    {
        var result = await host.RunOrchestrationAsync("OneAtATime", null, "parent-2");

        result.Succeeded.ShouldBeTrue(result.Describe());
        var children = host.Trace.SubOrchestrationsOf("parent-2");
        children[1].StartedSequence.ShouldBeGreaterThan(children[0].CompletedSequence);
    }

    [Fact]
    public async Task A_child_failure_reaches_the_parent_as_type_name_and_message_only()
    {
        var result = await host.RunOrchestrationAsync("WrapChildFailure", null);

        result.Succeeded.ShouldBeFalse();
        PipelineFailedException.TryParse(result.Failure!.ErrorType, result.Failure.ErrorMessage, out var code, out var detail).ShouldBeTrue();
        code.ShouldBe(PipelineErrorCodes.BatchStepFailed);
        detail.ShouldBe("FailWithCode failed (TXN_CODE_UNMAPPED): Unmapped transaction codes: P2PD");
    }

    [Fact]
    public async Task An_orchestrator_that_leaves_its_thread_fails_like_Durable_does()
    {
        var result = await host.RunOrchestrationAsync("LeavesItsThread", null);

        result.Succeeded.ShouldBeFalse();
        result.Failure!.ErrorType.ShouldBe(typeof(InvalidOperationException).FullName);
        result.Failure.ErrorMessage.ShouldStartWith("Multithreaded execution was detected");
    }

    [Fact]
    public async Task Durable_timers_advance_the_virtual_clock_that_activities_also_see()
    {
        var result = await host.RunOrchestrationAsync("WaitFiveMinutes", null);

        result.Succeeded.ShouldBeTrue(result.Describe());
        host.Converter.Deserialize<string>(result.SerializedOutput).ShouldBe("00:05:00|00:05:00");
    }

    [Fact]
    public async Task Unknown_or_mismatched_function_names_are_reported_with_the_available_ones()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => host.RunOrchestrationAsync("NoSuchPipeline", null));
        ex.Message.ShouldContain("EchoTwice");
        await Should.ThrowAsync<InvalidOperationException>(() => host.RunOrchestrationAsync("Echo", null));
    }

    [Fact]
    public async Task The_recording_client_captures_what_a_timer_function_schedules_and_the_host_runs_it()
    {
        var client = new RecordingDurableTaskClient(clock);

        await host.InvokeTimerAsync("SampleTimer", new TimerInfo(), client, TestContext.Current.CancellationToken);

        var scheduled = client.Scheduled.ShouldHaveSingleItem();
        scheduled.Name.ShouldBe("EchoTwice");
        scheduled.ScheduledAtUtc.ShouldBe(Start);
        Guid.TryParse(scheduled.InstanceId, out _).ShouldBeTrue();
        var result = await host.RunScheduledAsync(scheduled);
        result.Succeeded.ShouldBeTrue(result.Describe());
        host.Converter.Deserialize<SamplePayload>(result.SerializedOutput)!.Text.ShouldBe("timer!!");
    }

    [Fact]
    public void The_timer_emulator_fires_03_00_Pacific_once_a_day_across_both_2026_DST_changes()
    {
        var timer = host.Functions.Timer("SampleTimer");
        var emulator = TimerTriggerEmulator.For(timer, new Dictionary<string, string> { [TimerTriggerEmulator.TimeZoneSetting] = "America/Los_Angeles" });
        var golden = Scenario.Load("dst-transitions").Golden.Schedule!;

        foreach (var window in golden.Chunk(5))
        {
            var from = DateTimeOffset.Parse(window[0].FireTimeLocal, System.Globalization.CultureInfo.InvariantCulture).Date;
            var fromUtc = new DateTimeOffset(from, emulator.TimeZone.GetUtcOffset(from)).ToUniversalTime();
            var occurrences = emulator.Occurrences(fromUtc, fromUtc.AddDays(5));
            occurrences.Select(o => o.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture))
                .ShouldBe(window.Select(w => w.FireTimeUtc));
        }
    }

    [Fact]
    public void The_timer_emulator_skips_nonexistent_local_times_and_fires_repeated_ones_once()
    {
        var pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var springForward = new TimerTriggerEmulator("0 30 2 * * *", pacific)
            .Occurrences(new DateTimeOffset(2026, 3, 7, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 10, 8, 0, 0, TimeSpan.Zero));
        springForward.ShouldBe([new DateTimeOffset(2026, 3, 7, 10, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 9, 9, 30, 0, TimeSpan.Zero)]);

        var fallBack = new TimerTriggerEmulator("0 30 1 * * *", pacific)
            .Occurrences(new DateTimeOffset(2026, 11, 1, 7, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 2, 7, 0, 0, TimeSpan.Zero));
        fallBack.ShouldBe([new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero)], "the first (PDT) 01:30 only");
    }

    [Fact]
    public void The_timer_emulator_resolves_schedule_settings_and_defaults_to_UTC()
    {
        TimerTriggerEmulator.ReadLocalSettings(RepositoryPaths.FunctionsLocalSettings)[TimerTriggerEmulator.TimeZoneSetting].ShouldBe("America/Los_Angeles");
        var emulator = TimerTriggerEmulator.For(host.Functions.Timer("SampleTimer"), new Dictionary<string, string>());
        emulator.TimeZone.ShouldBe(TimeZoneInfo.Utc);
        emulator.Expression.ShouldBe("0 0 3 * * *");
    }
}
