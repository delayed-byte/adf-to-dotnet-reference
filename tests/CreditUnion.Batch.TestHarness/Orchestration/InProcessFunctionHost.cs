using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Converters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreditUnion.Batch.TestHarness.Orchestration;

/// <summary>The end state of an orchestration instance.</summary>
public sealed record OrchestrationResult(string Name, string InstanceId, bool Succeeded, string? SerializedOutput, TaskFailureDetails? Failure)
{
    /// <summary>One line per failure in the chain (outermost first), for assertion messages.</summary>
    public string Describe()
    {
        if (Succeeded)
        {
            return $"{Name} ({InstanceId}) completed.";
        }

        var lines = new List<string> { $"{Name} ({InstanceId}) failed:" };
        for (var f = Failure; f is not null; f = f.InnerFailure)
        {
            lines.Add($"  {f.ErrorType}: {f.ErrorMessage}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Hosts a Functions app's orchestrators, activities and timer trigger in the test process, with the
/// app's own service registrations (ADR 0008). Functions are found by their <c>[Function]</c> names;
/// each invocation gets a DI scope, and function classes are created per invocation as the isolated
/// worker does. This replaces the Functions host and the Durable Task backend, not the batch's code.
/// </summary>
public sealed class InProcessFunctionHost(FunctionCatalog functions, IServiceProvider services, VirtualClock clock)
{
    public FunctionCatalog Functions { get; } = functions ?? throw new ArgumentNullException(nameof(functions));

    public IServiceProvider Services { get; } = services ?? throw new ArgumentNullException(nameof(services));

    public VirtualClock Clock { get; } = clock ?? throw new ArgumentNullException(nameof(clock));

    public OrchestrationTrace Trace { get; } = new();

    public DataConverter Converter { get; } = JsonDataConverter.Default;

    public ILoggerFactory LoggerFactory => Services.GetRequiredService<ILoggerFactory>();

    /// <summary>Starts an orchestration as a client would (input serialized to JSON) and waits for it to finish.</summary>
    public Task<OrchestrationResult> RunOrchestrationAsync(string name, object? input, string? instanceId = null) =>
        RunSerializedAsync(name, Converter.Serialize(input), instanceId ?? Guid.NewGuid().ToString("N"), parent: null);

    /// <summary>Runs an orchestration whose input is already serialized (e.g. recorded by <see cref="RecordingDurableTaskClient"/>).</summary>
    public Task<OrchestrationResult> RunScheduledAsync(ScheduledOrchestration scheduled)
    {
        ArgumentNullException.ThrowIfNull(scheduled);
        return RunSerializedAsync(scheduled.Name, scheduled.SerializedInput, scheduled.InstanceId, parent: null);
    }

    /// <summary>Invokes the timer-triggered function <paramref name="name"/> as the Functions host would at a scheduled occurrence.</summary>
    public async Task InvokeTimerAsync(string name, TimerInfo timer, DurableTaskClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timer);
        ArgumentNullException.ThrowIfNull(client);
        var function = Functions.Timer(name);
        await using var scope = Services.CreateAsyncScope();
        var context = new TestFunctionContext(function.Name, scope.ServiceProvider, cancellationToken);
        await InvokeAsync(function, scope.ServiceProvider, p =>
            p.GetCustomAttribute<TimerTriggerAttribute>() is not null ? timer
            : p.GetCustomAttribute<DurableClientAttribute>() is not null ? client
            : Common(p, context, cancellationToken)).ConfigureAwait(false);
    }

    internal async Task<(string? Output, TaskFailureDetails? Failure)> ExecuteActivityAsync(string name, string? serializedInput)
    {
        try
        {
            var function = Functions.Activity(name);
            await using var scope = Services.CreateAsyncScope();
            var context = new TestFunctionContext(function.Name, scope.ServiceProvider, CancellationToken.None);
            var output = await InvokeAsync(function, scope.ServiceProvider, p =>
                p.GetCustomAttribute<ActivityTriggerAttribute>() is not null
                    ? serializedInput is null ? null : Converter.Deserialize(serializedInput, p.ParameterType)
                    : Common(p, context, CancellationToken.None)).ConfigureAwait(false);
            return (Converter.Serialize(output), null);
        }
        catch (Exception ex)
        {
            return (null, FailureDetailsOf(ex));
        }
    }

    internal async Task<(string? Output, TaskFailureDetails? Failure)> ExecuteSubOrchestrationAsync(string name, string instanceId, string? serializedInput, ParentOrchestrationInstance parent)
    {
        var result = await RunSerializedAsync(name, serializedInput, instanceId, parent).ConfigureAwait(false);
        return (result.SerializedOutput, result.Failure);
    }

    internal static TaskFailureDetails FailureDetailsOf(Exception exception) => exception switch
    {
        TaskFailedException failed => new TaskFailureDetails(failed.GetType().FullName!, failed.Message, failed.StackTrace, failed.FailureDetails, null),
        _ => new TaskFailureDetails(exception.GetType().FullName!, exception.Message, exception.StackTrace,
            exception.InnerException is { } inner ? FailureDetailsOf(inner) : null, null),
    };

    private async Task<OrchestrationResult> RunSerializedAsync(string name, string? serializedInput, string instanceId, ParentOrchestrationInstance? parent)
    {
        var function = Functions.Orchestrator(name);
        var started = Trace.NextSequence();
        var result = await OrchestrationPump.Run(function.Name + " " + instanceId, async pump =>
        {
            var context = new InProcessOrchestrationContext(this, pump, function.Name, instanceId, parent, serializedInput);
            try
            {
                var functionContext = new TestFunctionContext(function.Name, Services, CancellationToken.None);
                var output = await InvokeAsync(function, Services, p =>
                    p.GetCustomAttribute<OrchestrationTriggerAttribute>() is not null ? context : Common(p, functionContext, CancellationToken.None));
                return new OrchestrationResult(function.Name, instanceId, true, Converter.Serialize(output), null);
            }
            catch (Exception ex)
            {
                return new OrchestrationResult(function.Name, instanceId, false, null, FailureDetailsOf(ex));
            }
        }).ConfigureAwait(false);
        Trace.Add(new OrchestrationRecord(function.Name, instanceId, parent?.InstanceId, started, Trace.NextSequence(), result.Succeeded));
        return result;
    }

    private static object? Common(ParameterInfo parameter, FunctionContext context, CancellationToken cancellationToken)
    {
        if (parameter.ParameterType == typeof(FunctionContext))
        {
            return context;
        }

        if (parameter.ParameterType == typeof(CancellationToken))
        {
            return cancellationToken;
        }

        throw new NotSupportedException(
            $"Parameter '{parameter.Name}' ({parameter.ParameterType.Name}) of {parameter.Member.DeclaringType?.Name}.{parameter.Member.Name} has no in-process binding. " +
            "Supported: the trigger parameter, [DurableClient] DurableTaskClient (timer), FunctionContext and CancellationToken; take services through the constructor.");
    }

    // Not ConfigureAwait(false): an orchestrator's continuations must stay on its pump thread.
    private static async Task<object?> InvokeAsync(FunctionEntry function, IServiceProvider services, Func<ParameterInfo, object?> bind)
    {
        var method = function.Method;
        var target = method.IsStatic ? null : ActivatorUtilities.CreateInstance(services, method.DeclaringType!);
        try
        {
            var arguments = method.GetParameters().Select(bind).ToArray();
            object? returned;
            try
            {
                returned = method.Invoke(target, arguments);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }

            return await UnwrapAsync(method.ReturnType, returned);
        }
        finally
        {
            switch (target)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    private static async Task<object?> UnwrapAsync(Type returnType, object? returned)
    {
        switch (returned)
        {
            case Task task:
                await task;
                return returnType.IsGenericType ? returnType.GetProperty(nameof(Task<object>.Result))!.GetValue(task) : null;
            case ValueTask valueTask:
                await valueTask;
                return null;
            default:
                if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
                {
                    var asTask = (Task)returnType.GetMethod(nameof(ValueTask<object>.AsTask))!.Invoke(returned, null)!;
                    await asTask;
                    return asTask.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(asTask);
                }

                return returned;
        }
    }
}
