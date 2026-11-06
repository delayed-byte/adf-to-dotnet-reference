using System.Text.Json;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.Core.Reconciliation;
using CreditUnion.Batch.Functions.Activities;
using CreditUnion.Batch.TestHarness.Adf;
using CreditUnion.Batch.TestHarness.Orchestration;
using Microsoft.Extensions.DependencyInjection;

namespace CreditUnion.Batch.UnitTests.Reconciliation;

/// <summary>
/// PL_Reconciliation as the in-process Durable host runs it (ADR 0008), with the reporting store and the webhook replaced by
/// fakes. The fake store applies the same rule as <c>ops.usp_RecordReconExceptions</c> and <c>ops.usp_MarkReconAlertSent</c>
/// (a known exception adds no row; an alert is due while a current exception is unalerted), so the alert delivery semantics of
/// ADR 0013 can be shown end to end. AT-12 and AT-13 run the same orchestrator against the real procedures.
/// </summary>
public sealed class ReconOrchestratorTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 11, 0, 0, TimeSpan.Zero);
    private static readonly Guid RunId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly ReconciliationInput Input = new(new DateOnly(2026, 11, 4), RunId, 0.00m);
    private static readonly AdfActivityPolicy PostPolicy = AdfActivityPolicy.Read(PipelineNames.Reconciliation, "Post Recon Alert");

    private readonly FakeReconStore store = new();
    private readonly FakeAlertClient webhook = new();
    private readonly VirtualClock clock = new(Start);
    private readonly InProcessFunctionHost host;

    public ReconOrchestratorTests()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton<IReconciliationStore>(store)
            .AddSingleton<IReconAlertClient>(webhook)
            .AddSingleton(ReconActivityPolicies.Adf)
            .AddTransient<ReconciliationSteps>()
            .BuildServiceProvider();
        host = new InProcessFunctionHost(FunctionCatalog.FromAssembly(typeof(ReconciliationActivities).Assembly), services, clock);
    }

    private Task<OrchestrationResult> RunAsync(ReconciliationInput? input = null) => host.RunOrchestrationAsync(PipelineNames.Reconciliation, input ?? Input);

    [Fact]
    public async Task When_everything_reconciles_the_run_logs_Started_and_Reconciled_and_posts_nothing()
    {
        var result = await RunAsync();

        result.Succeeded.ShouldBeTrue(result.Describe());
        store.Calls.Where(c => c is not "card" and not "gl").ShouldBe(["log Started", "record", "log Reconciled"]);
        store.Calls.Order().Where(c => c is "card" or "gl").ShouldBe(["card", "gl"]);
        webhook.Attempts.ShouldBeEmpty();
        store.Events[0].ShouldBe(new LogBatchRunEventRequest(RunId, Input.BusinessDate, PipelineNames.Reconciliation, "Started", StartedUtc: Start.UtcDateTime));
        store.Events[1].ShouldBe(new LogBatchRunEventRequest(RunId, Input.BusinessDate, PipelineNames.Reconciliation, "Reconciled",
            RecordCount: 0, Notes: "card difference=0.00; GL items out of tolerance=0; tolerance=0.00", EndedUtc: Start.UtcDateTime));
        host.Trace.RetryDecisions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Both_lookups_are_scheduled_before_either_finishes_and_Record_Recon_Exceptions_waits_for_both()
    {
        var result = await RunAsync();

        result.Succeeded.ShouldBeTrue(result.Describe());
        var attempts = host.Trace.Attempts;
        var card = attempts.Single(a => a.Name == ReconActivityNames.LookupCardTotals);
        var gl = attempts.Single(a => a.Name == ReconActivityNames.LookupGlTotals);
        var record = attempts.Single(a => a.Name == ReconActivityNames.RecordExceptions);
        var started = attempts.First(a => a.Name == ReconActivityNames.LogEvent);
        Math.Max(card.ScheduledSequence, gl.ScheduledSequence).ShouldBeLessThan(Math.Min(card.CompletedSequence, gl.CompletedSequence), "Task.WhenAll: both are in flight together");
        Math.Min(card.ScheduledSequence, gl.ScheduledSequence).ShouldBeGreaterThan(started.CompletedSequence, "they depend on Log Recon Started");
        record.ScheduledSequence.ShouldBeGreaterThan(Math.Max(card.CompletedSequence, gl.CompletedSequence));
    }

    [Fact]
    public async Task A_card_difference_records_the_exception_posts_one_alert_marks_it_sent_and_logs_Exceptions()
    {
        store.Card = Card(0.01m);
        store.Current = ["CardSettlement ALL difference 0.01"];

        var result = await RunAsync();

        result.Succeeded.ShouldBeTrue(result.Describe());
        store.Calls.Where(c => c is not "card" and not "gl").ShouldBe(["log Started", "record", "mark", "log Exceptions"]);
        store.Calls.IndexOf("record").ShouldBeGreaterThan(store.Calls.IndexOf("gl"));
        webhook.Delivered.ShouldHaveSingleItem();
        using var body = JsonDocument.Parse(webhook.Delivered[0]);
        body.RootElement.GetProperty("cardDifference").GetDecimal().ShouldBe(0.01m);
        body.RootElement.GetProperty("batchRunId").GetString().ShouldBe(RunId.ToString("D"));
        body.RootElement.GetProperty("exceptionCount").GetInt32().ShouldBe(1);
        body.RootElement.GetProperty("summary").GetString().ShouldBe("CardSettlement ALL difference 0.01");
        store.Marked.ShouldBe([new MarkReconAlertSentRequest(Input.BusinessDate, RunId)]);
        store.Events[1].Status.ShouldBe("Exceptions");
        store.Events[1].RecordCount.ShouldBe(1);
        store.Events[1].Notes.ShouldBe("card difference=0.01; GL items out of tolerance=0; tolerance=0.00");
    }

    [Fact]
    public async Task The_alert_is_posted_before_it_is_marked_sent()
    {
        store.Current = ["GeneralLedger 1010:DEBIT difference 0.01"];

        await RunAsync();

        var post = host.Trace.Attempts.Single(a => a.Name == ReconActivityNames.PostAlert);
        var mark = host.Trace.Attempts.Single(a => a.Name == ReconActivityNames.MarkAlertSent);
        mark.ScheduledSequence.ShouldBeGreaterThan(post.CompletedSequence);
    }

    [Fact]
    public async Task A_rerun_after_a_delivered_alert_posts_nothing_even_under_a_new_batch_run_id()
    {
        store.Current = ["CardSettlement ALL difference 0.01"];
        (await RunAsync()).Succeeded.ShouldBeTrue();

        var rerun = await RunAsync(Input with { BatchRunId = Guid.NewGuid() });

        rerun.Succeeded.ShouldBeTrue(rerun.Describe());
        webhook.Delivered.Count.ShouldBe(1, "ops.usp_RecordReconExceptions says no alert is due: the exception is known and alerted");
        store.Inserted.ShouldBe(1, "and it adds no second row");
        store.Events.Last().Status.ShouldBe("Exceptions");
    }

    [Fact]
    public async Task A_changed_difference_is_a_new_exception_and_alerts_again()
    {
        store.Current = ["CardSettlement ALL difference 0.01"];
        await RunAsync();
        store.Current = ["CardSettlement ALL difference 0.02"];

        await RunAsync(Input with { BatchRunId = Guid.NewGuid() });

        webhook.Delivered.Count.ShouldBe(2);
        store.Inserted.ShouldBe(2);
    }

    [Fact]
    public async Task A_transient_webhook_failure_three_times_is_retried_thirty_seconds_apart_and_the_alert_is_delivered_once()
    {
        store.Current = ["CardSettlement ALL difference 0.01"];
        for (var i = 0; i < 3; i++)
        {
            webhook.Failures.Enqueue(new ReconAlertUnavailableException("HTTP 503"));
        }

        var result = await RunAsync();

        result.Succeeded.ShouldBeTrue(result.Describe());
        webhook.Attempts.Count.ShouldBe(4, "retry 3 = four attempts");
        webhook.Delivered.Count.ShouldBe(1);
        var failed = host.Trace.FailedAttempts;
        failed.Count.ShouldBe(3);
        failed.ShouldAllBe(a => a.Name == ReconActivityNames.PostAlert && PostPolicy.IsImplementedBy(a.Policy));
        host.Trace.RetryDecisions.Where(d => d.Delay is not null).Select(d => d.Delay).ShouldAllBe(d => d == TimeSpan.FromSeconds(30));
        store.Marked.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_webhook_that_stays_down_fails_the_run_leaves_the_exception_unalerted_and_the_next_run_alerts_it_once()
    {
        store.Current = ["CardSettlement ALL difference 0.01"];
        for (var i = 0; i < 4; i++)
        {
            webhook.Failures.Enqueue(new TimeoutException("no answer"));
        }

        var result = await RunAsync();

        result.Succeeded.ShouldBeFalse();
        webhook.Attempts.Count.ShouldBe(4);
        store.Marked.ShouldBeEmpty("never marked sent without a delivered POST");
        store.Calls.ShouldNotContain("log Exceptions", "Log Recon Outcome depends on the If succeeding");
        store.Inserted.ShouldBe(1);

        var next = await RunAsync(Input with { BatchRunId = Guid.NewGuid() });

        next.Succeeded.ShouldBeTrue(next.Describe());
        webhook.Delivered.Count.ShouldBe(1, "the exception was never alerted, so the rerun alerts it, once");
        store.Inserted.ShouldBe(1, "and the exception row is not duplicated");
        var third = await RunAsync(Input with { BatchRunId = Guid.NewGuid() });
        third.Succeeded.ShouldBeTrue();
        webhook.Delivered.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(typeof(ReconAlertRejectedException))]
    [InlineData(typeof(ReconAlertConfigurationException))]
    public async Task A_rejected_request_or_a_missing_URL_is_not_retried_and_marks_nothing(Type exception)
    {
        store.Current = ["CardSettlement ALL difference 0.01"];
        webhook.Failures.Enqueue((Exception)Activator.CreateInstance(exception, "refused")!);

        var result = await RunAsync();

        result.Succeeded.ShouldBeFalse();
        webhook.Attempts.Count.ShouldBe(1);
        host.Trace.RetryDecisions.Where(d => d.Delay is not null).ShouldBeEmpty();
        store.Marked.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_POST_that_was_delivered_but_timed_out_is_retried_and_so_delivered_twice_with_the_same_body()
    {
        // At-least-once, the best a webhook without an idempotency key allows (ADR 0013): the receiver can de-duplicate
        // on (businessDate, batchRunId), which the body carries and which is identical across attempts.
        store.Current = ["CardSettlement ALL difference 0.01"];
        webhook.DeliverThenTimeOutOnce = true;

        var result = await RunAsync();

        result.Succeeded.ShouldBeTrue(result.Describe());
        webhook.Delivered.Count.ShouldBe(2);
        webhook.Delivered[0].ShouldBe(webhook.Delivered[1]);
        store.Marked.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_Mark_after_a_delivered_POST_retries_only_the_Mark_and_never_posts_again()
    {
        store.Current = ["CardSettlement ALL difference 0.01"];
        store.FailMarkTimes = 2;

        var result = await RunAsync();

        result.Succeeded.ShouldBeTrue(result.Describe());
        webhook.Delivered.Count.ShouldBe(1);
        host.Trace.Attempts.Count(a => a.Name == ReconActivityNames.MarkAlertSent).ShouldBe(3, "retry 2 = three attempts");
        host.Trace.Attempts.Count(a => a.Name == ReconActivityNames.PostAlert).ShouldBe(1);
    }

    [Fact]
    public async Task A_Mark_that_keeps_failing_fails_the_run_and_the_next_run_posts_the_alert_again()
    {
        // What the code cannot guarantee (ADR 0013): the POST was delivered but could not be recorded, so a rerun repeats it.
        store.Current = ["CardSettlement ALL difference 0.01"];
        store.FailMarkTimes = 3;

        var result = await RunAsync();
        var next = await RunAsync(Input with { BatchRunId = Guid.NewGuid() });

        result.Succeeded.ShouldBeFalse();
        next.Succeeded.ShouldBeTrue(next.Describe());
        webhook.Delivered.Count.ShouldBe(2, "at-least-once: a lost alert is worse than a repeated one");
        store.Inserted.ShouldBe(1);
    }

    [Fact]
    public async Task A_Record_Recon_Exceptions_attempt_that_committed_but_lost_its_reply_still_alerts_exactly_once_on_the_retry()
    {
        store.Current = ["CardSettlement ALL difference 0.01"];
        store.LoseRecordReplyOnce = true;

        var result = await RunAsync();

        result.Succeeded.ShouldBeTrue(result.Describe());
        host.Trace.Attempts.Count(a => a.Name == ReconActivityNames.RecordExceptions).ShouldBe(2);
        store.Inserted.ShouldBe(1, "the retry finds the row and adds none");
        webhook.Delivered.Count.ShouldBe(1, "AlertRequired does not depend on whether this attempt inserted the row");
    }

    [Fact]
    public async Task A_transient_lookup_failure_is_retried_thirty_seconds_later_and_the_run_succeeds()
    {
        store.FailCardOnce = true;

        var result = await RunAsync();

        result.Succeeded.ShouldBeTrue(result.Describe());
        var attempts = host.Trace.Attempts.Where(a => a.Name == ReconActivityNames.LookupCardTotals).ToList();
        attempts.Count.ShouldBe(2);
        host.Trace.RetryDecisionAfter(attempts[0])!.Delay.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("0.005")]
    public async Task A_tolerance_the_procedures_cannot_honour_exactly_fails_without_retry_before_any_exception_is_recorded(string unrepresentable)
    {
        var tolerance = decimal.Parse(unrepresentable, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture);

        var result = await RunAsync(Input with { Tolerance = tolerance });

        result.Succeeded.ShouldBeFalse();
        result.Failure!.InnerFailure!.ErrorType.ShouldBe(typeof(ReconInputException).FullName);
        host.Trace.RetryDecisions.Where(d => d.Delay is not null).ShouldBeEmpty();
        store.Calls.ShouldNotContain("record");
    }

    [Fact]
    public async Task The_tolerance_reaches_the_procedures_and_the_notes_with_its_scale()
    {
        var result = await RunAsync(Input with { Tolerance = 0.25m });

        result.Succeeded.ShouldBeTrue(result.Describe());
        store.Tolerances.ShouldAllBe(t => t == 0.25m);
        store.Tolerances.Count.ShouldBe(3, "both lookups and the recording");
        store.Events[1].Notes.ShouldEndWith("tolerance=0.25");
    }

    private static CardReconTotals Card(decimal difference) => new(difference, 0.00m, difference, Math.Abs(difference), difference != 0m);

    private sealed class FakeAlertClient : IReconAlertClient
    {
        public Queue<Exception> Failures { get; } = new();

        public List<string> Attempts { get; } = [];

        public List<string> Delivered { get; } = [];

        public bool DeliverThenTimeOutOnce { get; set; }

        public Task PostAsync(string jsonBody, CancellationToken cancellationToken)
        {
            Attempts.Add(jsonBody);
            if (Failures.TryDequeue(out var failure))
            {
                return Task.FromException(failure);
            }

            Delivered.Add(jsonBody);
            if (DeliverThenTimeOutOnce)
            {
                DeliverThenTimeOutOnce = false;
                return Task.FromException(new TimeoutException("the response never arrived"));
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>The recon tables with the same alert rule as the procedures; <see cref="Current"/> are the out-of-tolerance items today.</summary>
    private sealed class FakeReconStore : IReconciliationStore
    {
        private readonly Lock gate = new();
        private readonly Dictionary<string, bool> known = [];

        public List<string> Calls { get; } = [];

        public List<LogBatchRunEventRequest> Events { get; } = [];

        public List<MarkReconAlertSentRequest> Marked { get; } = [];

        public List<decimal> Tolerances { get; } = [];

        public CardReconTotals Card { get; set; } = new(0.00m, 0.00m, 0.00m, 0.00m, false);

        public GlReconTotals Gl { get; set; } = new(5, 0, 0.00m, false);

        public IReadOnlyList<string> Current { get; set; } = [];

        public int Inserted => known.Count;

        public int FailMarkTimes { get; set; }

        public bool FailCardOnce { get; set; }

        public bool LoseRecordReplyOnce { get; set; }

        public Task LogBatchRunEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("log " + request.Status);
            Events.Add(request);
            return Task.CompletedTask;
        }

        public Task<CardReconTotals> GetCardTotalsAsync(ReconQuery query, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                Calls.Add("card");
                Tolerances.Add(query.Tolerance);
            }

            if (FailCardOnce)
            {
                FailCardOnce = false;
                return Task.FromException<CardReconTotals>(new TimeoutException("deadlock victim"));
            }

            return Task.FromResult(Card);
        }

        public Task<GlReconTotals> GetGlTotalsAsync(ReconQuery query, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                Calls.Add("gl");
                Tolerances.Add(query.Tolerance);
            }

            return Task.FromResult(Gl);
        }

        public Task<ReconExceptionResult> RecordExceptionsAsync(RecordReconExceptionsRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("record");
            Tolerances.Add(request.Tolerance);
            var added = 0;
            foreach (var item in Current.Where(c => !known.ContainsKey(c)))
            {
                known[item] = false;
                added++;
            }

            if (LoseRecordReplyOnce)
            {
                LoseRecordReplyOnce = false;
                return Task.FromException<ReconExceptionResult>(new TimeoutException("committed, but the reply was lost"));
            }

            return Task.FromResult(new ReconExceptionResult(Current.Count > 0, Current.Count, added, Current.Any(c => !known[c]), string.Join("; ", Current.Order(StringComparer.Ordinal))));
        }

        public Task MarkAlertSentAsync(MarkReconAlertSentRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("mark");
            if (FailMarkTimes > 0)
            {
                FailMarkTimes--;
                return Task.FromException(new TimeoutException("sql down"));
            }

            Marked.Add(request);
            foreach (var key in known.Keys.ToList())
            {
                known[key] = true;
            }

            return Task.CompletedTask;
        }
    }
}
