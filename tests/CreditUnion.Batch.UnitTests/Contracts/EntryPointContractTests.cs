using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.Functions;
using CreditUnion.Batch.TestData.Golden;
using CreditUnion.Batch.TestHarness.Orchestration;
using Microsoft.DurableTask.Converters;

namespace CreditUnion.Batch.UnitTests.Contracts;

/// <summary>
/// The public orchestration entry points the acceptance tests are written against (ADR 0008): one
/// orchestrator per ADF pipeline under the pipeline's name, the trigger under the trigger's name,
/// typed inputs that survive Durable's JSON serialization, and the error-code contract.
/// </summary>
public sealed class EntryPointContractTests
{
    private static readonly FunctionCatalog App = FunctionCatalog.FromAssembly(typeof(BatchServiceCollectionExtensions).Assembly);

    [Fact]
    public void Every_ADF_pipeline_is_an_orchestrator_function_of_the_same_name()
    {
        var adfPipelines = Directory.GetFiles(Path.Combine(RepoPaths.Adf, "pipeline"), "*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal);

        PipelineNames.All.Order(StringComparer.Ordinal).ShouldBe(adfPipelines!);
        PipelineNames.All.Order(StringComparer.Ordinal).ShouldBe(Pipelines.Children.Prepend(Pipelines.Master).Order(StringComparer.Ordinal), "the oracle's names");
        foreach (var name in PipelineNames.All)
        {
            App.Orchestrator(name).Name.ShouldBe(name);
        }
    }

    [Fact]
    public void The_schedule_trigger_is_a_timer_function_of_the_same_name()
    {
        Path.GetFileNameWithoutExtension(Directory.GetFiles(Path.Combine(RepoPaths.Adf, "trigger"), "*.json").Single()).ShouldBe(TriggerNames.Nightly);
        App.Timer(TriggerNames.Nightly).Name.ShouldBe(TriggerNames.Nightly);
    }

    [Fact]
    public void Inputs_survive_Durable_json_serialization_with_dates_ids_and_decimal_scale()
    {
        var converter = JsonDataConverter.Default;
        var id = Guid.NewGuid();

        converter.Deserialize<PipelineRunInput>(converter.Serialize(new PipelineRunInput(new DateOnly(2026, 11, 4), id)))
            .ShouldBe(new PipelineRunInput(new DateOnly(2026, 11, 4), id));
        var recon = converter.Deserialize<ReconciliationInput>(converter.Serialize(new ReconciliationInput(new DateOnly(2026, 11, 4), id, 0.00m)))!;
        recon.Tolerance.ToString(System.Globalization.CultureInfo.InvariantCulture).ShouldBe("0.00");

        var master = new NightlyBatchInput { BusinessDate = new DateOnly(2026, 3, 7), TriggerName = TriggerNames.Nightly, TriggerTimeUtc = new DateTimeOffset(2026, 3, 8, 10, 0, 0, TimeSpan.Zero) };
        converter.Deserialize<NightlyBatchInput>(converter.Serialize(master)).ShouldBe(master);
        new NightlyBatchInput().ReconTolerance.ToString(System.Globalization.CultureInfo.InvariantCulture).ShouldBe("0.00");
    }

    [Fact]
    public void Error_codes_travel_in_the_message_and_are_read_back_from_the_type_name_and_message()
    {
        var ex = new PipelineFailedException(PipelineErrorCodes.TxnCodeUnmapped, "Unmapped transaction codes: P2PD");

        ex.Message.ShouldBe("TXN_CODE_UNMAPPED: Unmapped transaction codes: P2PD");
        PipelineFailedException.TryParse(typeof(PipelineFailedException).FullName, ex.Message, out var code, out var detail).ShouldBeTrue();
        (code, detail).ShouldBe(("TXN_CODE_UNMAPPED", "Unmapped transaction codes: P2PD"));
        PipelineFailedException.TryParse(typeof(InvalidOperationException).FullName, ex.Message, out _, out _).ShouldBeFalse("only a PipelineFailedException carries a code");
        Should.Throw<ArgumentException>(() => new PipelineFailedException("A: B", "x"));
    }
}
