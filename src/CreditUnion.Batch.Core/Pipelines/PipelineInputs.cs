namespace CreditUnion.Batch.Core.Pipelines;

/// <summary>
/// Parameters of PL_ExtractKeyStone, PL_IngestCardSettlement and PL_MapAndAggregate as the master passes
/// them (businessDate, batchRunId). Child parameters that no caller overrides (container names, the
/// KeyBridge base URL, retainUntil for archived files) are configuration, not input (ADR 0008).
/// </summary>
public sealed record PipelineRunInput(DateOnly BusinessDate, Guid BatchRunId);

/// <summary>Parameters of PL_Reconciliation. <see cref="Tolerance"/> keeps its scale (0.00), as the run notes print it.</summary>
public sealed record ReconciliationInput(DateOnly BusinessDate, Guid BatchRunId, decimal Tolerance);

/// <summary>
/// Parameters of PL_Master_NightlyBatch. The master's batchRunId is its orchestration instance ID
/// (ADF: <c>pipeline().RunId</c>), which must therefore be a GUID.
/// </summary>
public sealed record NightlyBatchInput
{
    /// <summary>
    /// The Pacific business date to process. The schedule trigger always sets it (the previous Pacific
    /// day of its fire time); null means the previous Pacific day of <see cref="TriggerTimeUtc"/>.
    /// </summary>
    public DateOnly? BusinessDate { get; init; }

    /// <summary>ADF parameter reconTolerance (default "0.00"); money, so decimal with its scale.</summary>
    public decimal ReconTolerance { get; init; } = 0.00m;

    /// <summary>ADF parameter retainUntil: the retain-until tag on the evidence file; a placeholder until the records policy sets it.</summary>
    public string RetainUntil { get; init; } = "TBD";

    /// <summary>ADF <c>pipeline().TriggerName</c>; null for a manual run.</summary>
    public string? TriggerName { get; init; }

    /// <summary>ADF <c>pipeline().TriggerTime</c> (the trigger's scheduled time); null for a manual run.</summary>
    public DateTimeOffset? TriggerTimeUtc { get; init; }
}
