using Microsoft.Azure.Functions.Worker;

namespace CreditUnion.Batch.TestHarness.Orchestration;

/// <summary>
/// The part of <see cref="FunctionContext"/> a function body can rely on in process: its services
/// (so <c>context.GetLogger(...)</c> works), items and cancellation. Binding and invocation metadata
/// belong to the Functions host and are not available.
/// </summary>
internal sealed class TestFunctionContext(string functionName, IServiceProvider services, CancellationToken cancellationToken) : FunctionContext
{
    public override string InvocationId { get; } = Guid.NewGuid().ToString("D");

    public override string FunctionId => functionName;

    public override TraceContext TraceContext => throw Unavailable(nameof(TraceContext));

    public override BindingContext BindingContext => throw Unavailable(nameof(BindingContext));

    public override Microsoft.Azure.Functions.Worker.RetryContext RetryContext => throw Unavailable(nameof(RetryContext));

    public override IServiceProvider InstanceServices { get; set; } = services;

    public override FunctionDefinition FunctionDefinition => throw Unavailable(nameof(FunctionDefinition));

    public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();

    public override IInvocationFeatures Features => throw Unavailable(nameof(Features));

    public override CancellationToken CancellationToken => cancellationToken;

    private static NotSupportedException Unavailable(string member) =>
        new($"FunctionContext.{member} is provided by the Functions host and is not available in the in-process test host.");
}
