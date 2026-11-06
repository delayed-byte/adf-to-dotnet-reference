using CreditUnion.Batch.Core.Pipelines;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;

namespace CreditUnion.Batch.Functions.Orchestrators;

// Public orchestration entry points (ADR 0008): one Durable orchestrator per ADF pipeline, registered
// under the pipeline's name and taking its typed input from context.GetInput<T>(). The acceptance
// tests (AT-01 to AT-19) are written against these names and inputs. Each body is replaced by the
// conversion in the build phase named in its exception.

/// <summary>PL_ExtractKeyStone: input <see cref="PipelineRunInput"/>.</summary>
public static class ExtractKeyStoneOrchestrator
{
    [Function(PipelineNames.Extract)]
    public static Task RunAsync([OrchestrationTrigger] TaskOrchestrationContext context) =>
        throw new NotImplementedException($"{PipelineNames.Extract} is not converted yet (docs/PRD.md, Phase 4).");
}

/// <summary>PL_IngestCardSettlement: input <see cref="PipelineRunInput"/>.</summary>
public static class IngestCardSettlementOrchestrator
{
    [Function(PipelineNames.CardSettlement)]
    public static Task RunAsync([OrchestrationTrigger] TaskOrchestrationContext context) =>
        throw new NotImplementedException($"{PipelineNames.CardSettlement} is not converted yet (docs/PRD.md, Phase 5).");
}

/// <summary>PL_MapAndAggregate: input <see cref="PipelineRunInput"/>.</summary>
public static class MapAndAggregateOrchestrator
{
    [Function(PipelineNames.MapAndAggregate)]
    public static Task RunAsync([OrchestrationTrigger] TaskOrchestrationContext context) =>
        throw new NotImplementedException($"{PipelineNames.MapAndAggregate} is not converted yet (docs/PRD.md, Phase 6).");
}

/// <summary>PL_Reconciliation: input <see cref="ReconciliationInput"/>.</summary>
public static class ReconciliationOrchestrator
{
    [Function(PipelineNames.Reconciliation)]
    public static Task RunAsync([OrchestrationTrigger] TaskOrchestrationContext context) =>
        throw new NotImplementedException($"{PipelineNames.Reconciliation} is not converted yet (docs/PRD.md, Phase 7).");
}

/// <summary>PL_Master_NightlyBatch: input <see cref="NightlyBatchInput"/>; the instance ID is the batchRunId.</summary>
public static class NightlyBatchOrchestrator
{
    [Function(PipelineNames.Master)]
    public static Task RunAsync([OrchestrationTrigger] TaskOrchestrationContext context) =>
        throw new NotImplementedException($"{PipelineNames.Master} is not converted yet (docs/PRD.md, Phase 8).");
}
