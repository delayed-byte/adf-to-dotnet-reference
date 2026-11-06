using System.Reflection;
using Microsoft.Azure.Functions.Worker;

namespace CreditUnion.Batch.TestHarness.Orchestration;

public enum FunctionKind
{
    Orchestrator,
    Activity,
    Timer,
    Other,
}

/// <summary>A function found in a Functions app assembly: its <c>[Function]</c> name, trigger kind and method.</summary>
public sealed record FunctionEntry(string Name, FunctionKind Kind, MethodInfo Method)
{
    public override string ToString() => $"{Kind} {Name} ({Method.DeclaringType?.Name}.{Method.Name})";
}

/// <summary>
/// The functions of a Functions app, discovered the way the worker's metadata generator finds them:
/// public methods with <see cref="FunctionAttribute"/>, classified by their trigger parameter. Names
/// are case-insensitive, as in the Functions host.
/// </summary>
public sealed class FunctionCatalog
{
    private readonly Dictionary<string, FunctionEntry> byName;

    private FunctionCatalog(Dictionary<string, FunctionEntry> byName)
    {
        this.byName = byName;
    }

    public IReadOnlyCollection<FunctionEntry> All => byName.Values;

    public static FunctionCatalog FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var byName = new Dictionary<string, FunctionEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in assembly.GetTypes().Where(t => t.IsPublic || t.IsNestedPublic))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttribute<FunctionAttribute>() is not { } function)
                {
                    continue;
                }

                var entry = new FunctionEntry(function.Name, KindOf(method), method);
                if (!byName.TryAdd(entry.Name, entry))
                {
                    throw new InvalidOperationException($"Two functions are named '{entry.Name}': {byName[entry.Name]} and {entry}.");
                }
            }
        }

        return new FunctionCatalog(byName);
    }

    public FunctionEntry Orchestrator(string name) => Get(name, FunctionKind.Orchestrator);

    public FunctionEntry Activity(string name) => Get(name, FunctionKind.Activity);

    /// <summary>The single timer-triggered function of the app.</summary>
    public FunctionEntry Timer(string name) => Get(name, FunctionKind.Timer);

    private FunctionEntry Get(string name, FunctionKind kind)
    {
        if (!byName.TryGetValue(name, out var entry))
        {
            throw new InvalidOperationException(
                $"No function named '{name}'. {kind} functions in the app: {string.Join(", ", byName.Values.Where(f => f.Kind == kind).Select(f => f.Name).Order(StringComparer.Ordinal))}.");
        }

        return entry.Kind == kind ? entry : throw new InvalidOperationException($"'{name}' is not a {kind} function: {entry}.");
    }

    private static FunctionKind KindOf(MethodInfo method)
    {
        foreach (var parameter in method.GetParameters())
        {
            if (parameter.GetCustomAttribute<OrchestrationTriggerAttribute>() is not null)
            {
                return FunctionKind.Orchestrator;
            }

            if (parameter.GetCustomAttribute<ActivityTriggerAttribute>() is not null)
            {
                return FunctionKind.Activity;
            }

            if (parameter.GetCustomAttribute<TimerTriggerAttribute>() is not null)
            {
                return FunctionKind.Timer;
            }
        }

        return FunctionKind.Other;
    }
}
