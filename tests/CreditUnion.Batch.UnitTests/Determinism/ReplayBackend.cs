using System.Reflection;
using System.Runtime.ExceptionServices;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.Functions;
using CreditUnion.Batch.TestHarness.Orchestration;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Converters;
using Microsoft.DurableTask.Protobuf;
using Microsoft.DurableTask.Worker.Grpc;

namespace CreditUnion.Batch.UnitTests.Determinism;

/// <summary>What an activity does on one attempt: return a value, or fail with an exception type and message.</summary>
public sealed record ActivityOutcome(object? Result, System.Type? FailureType = null, string? FailureMessage = null)
{
    public static ActivityOutcome Returns(object? result) => new(result);

    public static ActivityOutcome Fails<TException>(string message)
        where TException : Exception => new(null, typeof(TException), message);
}

/// <summary>One orchestration episode: the history the worker was given, the new events, and the actions it returned.</summary>
public sealed record Episode(IReadOnlyList<HistoryEvent> PastEvents, IReadOnlyList<HistoryEvent> NewEvents, OrchestratorResponse Response);

/// <summary>An orchestration instance as the backend ran it, episode by episode.</summary>
public sealed class RecordedOrchestration(string name, string instanceId)
{
    public string Name { get; } = name;

    public string InstanceId { get; } = instanceId;

    public List<Episode> Episodes { get; } = [];

    public CompleteOrchestrationAction? Completion { get; set; }

    public bool Succeeded => Completion?.OrchestrationStatus == OrchestrationStatus.Completed;
}

/// <summary>
/// A minimal Durable backend for the determinism check (ADR 0017). It runs orchestrator functions through
/// <see cref="GrpcOrchestrationRunner"/>, the isolated worker's own entry point for executing an orchestrator against a history
/// (DurableTask.Core's replay engine behind the SDK's shim), one episode at a time, as the Durable Functions host does:
/// every episode replays the whole history recorded so far and appends the new events. Activities are answered from a script;
/// sub-orchestrations are run recursively by the same backend; durable timers fire at their due time. Completions are
/// delivered one per episode, so every orchestrator is replayed many times. The histories it records are real Durable
/// histories, which <see cref="ReplayCheck"/> then replays again.
/// </summary>
public sealed class ReplayBackend
{
    private static readonly DataConverter Converter = JsonDataConverter.Default;
    private readonly Dictionary<string, Func<string, string>> orchestrators = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, string?, int, ActivityOutcome> activities;
    private readonly DateTime start;

    /// <param name="activities">(activity name, serialized input, attempt) to outcome. Attempts count calls with the same name and input.</param>
    public ReplayBackend(Func<string, string?, int, ActivityOutcome> activities, DateTime startUtc, IServiceProvider? services = null)
    {
        this.activities = activities ?? throw new ArgumentNullException(nameof(activities));
        start = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
        Services = services;
    }

    public IServiceProvider? Services { get; }

    public List<RecordedOrchestration> Recorded { get; } = [];

    /// <summary>The production orchestrators of the Functions app, with the input type each one reads.</summary>
    public static ReplayBackend ForFunctionsApp(Func<string, string?, int, ActivityOutcome> activities, DateTime startUtc, IServiceProvider? services = null)
    {
        var backend = new ReplayBackend(activities, startUtc, services);
        var catalog = FunctionCatalog.FromAssembly(typeof(BatchServiceCollectionExtensions).Assembly);
        backend.Register<NightlyBatchInput>(PipelineNames.Master, catalog.Orchestrator(PipelineNames.Master).Method);
        backend.Register<PipelineRunInput>(PipelineNames.Extract, catalog.Orchestrator(PipelineNames.Extract).Method);
        backend.Register<PipelineRunInput>(PipelineNames.CardSettlement, catalog.Orchestrator(PipelineNames.CardSettlement).Method);
        backend.Register<PipelineRunInput>(PipelineNames.MapAndAggregate, catalog.Orchestrator(PipelineNames.MapAndAggregate).Method);
        backend.Register<ReconciliationInput>(PipelineNames.Reconciliation, catalog.Orchestrator(PipelineNames.Reconciliation).Method);
        return backend;
    }

    /// <summary>Registers an orchestrator <c>[Function]</c> method (<c>Task RunAsync(TaskOrchestrationContext)</c>) whose input is <typeparamref name="TInput"/>.</summary>
    public void Register<TInput>(string name, MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        Register<TInput>(name, context => Invoke(method, context));
    }

    public void Register<TInput>(string name, Func<TaskOrchestrationContext, Task> orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        orchestrators[name] = request => GrpcOrchestrationRunner.LoadAndRun<TInput, object?>(request, async (context, _) =>
        {
            await orchestrator(context);
            return null;
        }, Services);
    }

    /// <summary>Runs one episode of <paramref name="name"/> on the given history, as the host would.</summary>
    public OrchestratorResponse Execute(string name, string instanceId, IEnumerable<HistoryEvent> pastEvents, IEnumerable<HistoryEvent> newEvents)
    {
        var request = new OrchestratorRequest { InstanceId = instanceId };
        request.PastEvents.AddRange(pastEvents.Select(e => e.Clone()));
        request.NewEvents.AddRange(newEvents.Select(e => e.Clone()));
        var encoded = orchestrators[name](Convert.ToBase64String(request.ToByteArray()));
        return OrchestratorResponse.Parser.ParseFrom(Convert.FromBase64String(encoded));
    }

    /// <summary>Runs an orchestration to completion and records it (and every sub-orchestration it starts).</summary>
    public RecordedOrchestration Run(string name, string instanceId, object? input) => Run(name, instanceId, Converter.Serialize(input), start, parentDepth: 0);

    private static Task Invoke(MethodInfo method, TaskOrchestrationContext context)
    {
        try
        {
            return (Task)method.Invoke(null, [context])!;
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private static HistoryEvent Event(int id, DateTime at, Action<HistoryEvent> set)
    {
        var e = new HistoryEvent { EventId = id, Timestamp = Timestamp.FromDateTime(at) };
        set(e);
        return e;
    }

    private static Microsoft.DurableTask.Protobuf.TaskFailureDetails Failure(string errorType, string message) =>
        new() { ErrorType = errorType, ErrorMessage = message, StackTrace = string.Empty };

    private RecordedOrchestration Run(string name, string instanceId, string? input, DateTime clock, int parentDepth)
    {
        if (parentDepth > 3)
        {
            throw new InvalidOperationException("Sub-orchestrations nest deeper than the batch does.");
        }

        var recorded = new RecordedOrchestration(name, instanceId);
        Recorded.Add(recorded);
        var history = new List<HistoryEvent>();
        var pending = new Queue<HistoryEvent>();
        var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
        List<HistoryEvent> newEvents =
        [
            Event(-1, clock, e => e.OrchestratorStarted = new OrchestratorStartedEvent()),
            Event(-1, clock, e => e.ExecutionStarted = new ExecutionStartedEvent
            {
                Name = name,
                Input = input,
                OrchestrationInstance = new OrchestrationInstance { InstanceId = instanceId, ExecutionId = "execution-1" },
            }),
        ];

        for (var episode = 0; ; episode++)
        {
            if (episode > 2000)
            {
                throw new InvalidOperationException($"{name} did not complete within 2000 episodes.");
            }

            var response = Execute(name, instanceId, history, newEvents);
            recorded.Episodes.Add(new Episode([.. history], [.. newEvents], response));
            history.AddRange(newEvents);
            foreach (var action in response.Actions)
            {
                switch (action.OrchestratorActionTypeCase)
                {
                    case OrchestratorAction.OrchestratorActionTypeOneofCase.ScheduleTask:
                    {
                        var task = action.ScheduleTask;
                        history.Add(Event(action.Id, clock, e => e.TaskScheduled = new TaskScheduledEvent { Name = task.Name, Input = task.Input }));
                        var key = task.Name + "|" + task.Input;
                        var attempt = attempts[key] = attempts.GetValueOrDefault(key) + 1;
                        var outcome = activities(task.Name, task.Input, attempt);
                        pending.Enqueue(outcome.FailureType is null
                            ? Event(-1, clock, e => e.TaskCompleted = new TaskCompletedEvent { TaskScheduledId = action.Id, Result = Converter.Serialize(outcome.Result) })
                            : Event(-1, clock, e => e.TaskFailed = new TaskFailedEvent { TaskScheduledId = action.Id, FailureDetails = Failure(outcome.FailureType.FullName!, outcome.FailureMessage ?? "failed") }));
                        break;
                    }

                    case OrchestratorAction.OrchestratorActionTypeOneofCase.CreateSubOrchestration:
                    {
                        var sub = action.CreateSubOrchestration;
                        history.Add(Event(action.Id, clock, e => e.SubOrchestrationInstanceCreated = new SubOrchestrationInstanceCreatedEvent { InstanceId = sub.InstanceId, Name = sub.Name, Input = sub.Input }));
                        var child = Run(sub.Name, sub.InstanceId, sub.Input, clock, parentDepth + 1);
                        pending.Enqueue(child.Succeeded
                            ? Event(-1, clock, e => e.SubOrchestrationInstanceCompleted = new SubOrchestrationInstanceCompletedEvent { TaskScheduledId = action.Id, Result = child.Completion!.Result })
                            : Event(-1, clock, e => e.SubOrchestrationInstanceFailed = new SubOrchestrationInstanceFailedEvent { TaskScheduledId = action.Id, FailureDetails = child.Completion!.FailureDetails }));
                        break;
                    }

                    case OrchestratorAction.OrchestratorActionTypeOneofCase.CreateTimer:
                    {
                        var fireAt = action.CreateTimer.FireAt;
                        history.Add(Event(action.Id, clock, e => e.TimerCreated = new TimerCreatedEvent { FireAt = fireAt }));
                        pending.Enqueue(Event(-1, clock, e => e.TimerFired = new TimerFiredEvent { TimerId = action.Id, FireAt = fireAt }));
                        break;
                    }

                    case OrchestratorAction.OrchestratorActionTypeOneofCase.CompleteOrchestration:
                        recorded.Completion = action.CompleteOrchestration;
                        break;

                    default:
                        throw new NotSupportedException($"{name} returned a {action.OrchestratorActionTypeCase} action, which the batch never uses.");
                }
            }

            history.Add(Event(-1, clock, e => e.OrchestratorCompleted = new OrchestratorCompletedEvent()));
            if (recorded.Completion is not null)
            {
                return recorded;
            }

            if (pending.Count == 0)
            {
                throw new InvalidOperationException($"{name} is waiting for nothing after episode {episode}.");
            }

            var next = pending.Dequeue();
            clock = next.TimerFired is { } fired && fired.FireAt.ToDateTime() > clock ? fired.FireAt.ToDateTime() : clock.AddSeconds(1);
            next.Timestamp = Timestamp.FromDateTime(clock);
            newEvents = [Event(-1, clock, e => e.OrchestratorStarted = new OrchestratorStartedEvent()), next];
        }
    }
}
