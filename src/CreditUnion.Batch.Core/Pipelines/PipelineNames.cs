namespace CreditUnion.Batch.Core.Pipelines;

/// <summary>
/// The ADF pipeline names (adf/pipeline). Each converted pipeline is a Durable orchestrator function
/// registered under the same name, and the name is what ops.BatchRun records as PipelineName (ADR 0008).
/// </summary>
public static class PipelineNames
{
    public const string Master = "PL_Master_NightlyBatch";
    public const string Extract = "PL_ExtractKeyStone";
    public const string CardSettlement = "PL_IngestCardSettlement";
    public const string MapAndAggregate = "PL_MapAndAggregate";
    public const string Reconciliation = "PL_Reconciliation";

    public static IReadOnlyList<string> All { get; } = [Master, Extract, CardSettlement, MapAndAggregate, Reconciliation];
}

/// <summary>The schedule trigger (adf/trigger/TR_Nightly_0300_Pacific.json), converted to a timer-triggered starter function of the same name.</summary>
public static class TriggerNames
{
    public const string Nightly = "TR_Nightly_0300_Pacific";
}
