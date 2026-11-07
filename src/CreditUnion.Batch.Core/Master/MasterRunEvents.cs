using System.Globalization;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Pipelines;

namespace CreditUnion.Batch.Core.Master;

/// <summary>Step names the master writes to <c>ops.BatchRun.StepName</c> in its Failed events ("Log Failed At …", ADF literals).</summary>
public static class MasterSteps
{
    /// <summary>"Log Failed At Evidence": <c>StepName = "ExportRunEvidence"</c>. The four child steps use the child pipeline's name.</summary>
    public const string ExportRunEvidence = "ExportRunEvidence";

    /// <summary>The prefix of "Fail At …"'s message for a step: <c>'{pipeline} failed: '</c>, or <c>'Run evidence export failed: '</c>.</summary>
    public static string FailMessagePrefix(string stepName) =>
        stepName == ExportRunEvidence ? "Run evidence export failed: " : stepName + " failed: ";
}

/// <summary>
/// One link of a failure chain as Durable reports it (<c>TaskFailureDetails</c>): only a type name and a message survive
/// the boundary (ADR 0008). Chains are listed outermost first.
/// </summary>
public sealed record FailureLink(string ErrorType, string ErrorMessage);

/// <summary>
/// What the master records when a step fails: the parameters of "Log Failed At …" (StepName, ErrorCode, ErrorMessage) and the
/// message of "Fail At …" (ADR 0014).
/// <list type="bullet">
/// <item><b>ErrorCode</b> (ADF <c>activity(…).error.errorCode</c>): the child's own code when its failure is a
/// <see cref="PipelineFailedException"/> anywhere in the chain (ADR 0007: e.g. TXN_CODE_UNMAPPED); otherwise the short type
/// name of the root cause (the innermost failure), e.g. <c>TimeoutException</c>, because .NET has no ADF numeric codes.</item>
/// <item><b>ErrorMessage</b> (ADF <c>activity(…).error.message</c>): the step's failure message as Durable reports it; for a
/// <see cref="PipelineFailedException"/> its detail, because the code is already in ErrorCode.</item>
/// </list>
/// Both are cut to the <c>ops.usp_LogBatchRunEvent</c> parameter sizes (varchar(50), nvarchar(2000)).
/// </summary>
public sealed record StepFailure(string StepName, string ErrorCode, string ErrorMessage)
{
    public const int MaxErrorCodeLength = 50;
    public const int MaxErrorMessageLength = 2000;

    /// <summary>The detail of the master's <c>BATCH_STEP_FAILED</c> (ADF "Fail At …": <c>concat('{step} failed: ', error.message)</c>).</summary>
    public string FailMessage => MasterSteps.FailMessagePrefix(StepName) + ErrorMessage;

    public static StepFailure From(string stepName, IReadOnlyList<FailureLink> chain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        ArgumentNullException.ThrowIfNull(chain);
        if (chain.Count == 0)
        {
            throw new ArgumentException("A failure chain has at least one link.", nameof(chain));
        }

        var outer = chain[0];
        if (PipelineFailedException.TryParse(outer.ErrorType, outer.ErrorMessage, out var code, out var detail))
        {
            return new StepFailure(stepName, Cut(code, MaxErrorCodeLength), Cut(detail, MaxErrorMessageLength));
        }

        string? inner = null;
        foreach (var link in chain.Skip(1))
        {
            if (PipelineFailedException.TryParse(link.ErrorType, link.ErrorMessage, out var innerCode, out _))
            {
                inner = innerCode;
                break;
            }
        }

        return new StepFailure(stepName, Cut(inner ?? ShortTypeName(chain[^1].ErrorType), MaxErrorCodeLength), Cut(outer.ErrorMessage, MaxErrorMessageLength));
    }

    /// <summary><c>Namespace.Outer+Inner</c> becomes <c>Inner</c>.</summary>
    public static string ShortTypeName(string errorType)
    {
        ArgumentNullException.ThrowIfNull(errorType);
        var cut = errorType.LastIndexOfAny(['.', '+']);
        var name = cut >= 0 && cut < errorType.Length - 1 ? errorType[(cut + 1)..] : errorType;
        return string.IsNullOrWhiteSpace(name) ? "UNKNOWN" : name;
    }

    private static string Cut(string value, int length) => value.Length <= length ? value : value[..length];
}

/// <summary>
/// The <c>ops.usp_LogBatchRunEvent</c> calls of PL_Master_NightlyBatch, as the ADF activities build their parameters.
/// <c>PipelineName</c> is <c>@pipeline().Pipeline</c>; timestamps are the orchestration's <c>CurrentUtcDateTime</c> (<c>@utcNow()</c>).
/// </summary>
public static class MasterRunEvents
{
    /// <summary>ADF's <c>pipeline().TriggerName</c> for a run started by hand ("Trigger now").</summary>
    public const string ManualTriggerName = "Manual";

    /// <summary>Log Batch Started.</summary>
    public static LogBatchRunEventRequest Started(Guid batchRunId, DateOnly businessDate, string triggerName, DateTimeOffset triggerTime, DateTime startedUtc) =>
        new(batchRunId, businessDate, PipelineNames.Master, "Started", Notes: StartedNotes(triggerName, triggerTime), StartedUtc: startedUtc);

    /// <summary>
    /// <c>@concat('trigger=', pipeline().TriggerName, '; triggerTimeUtc=', string(pipeline().TriggerTime))</c>. ADF prints a
    /// timestamp as ISO 8601 UTC with seven fraction digits (<c>2026-11-05T11:00:00.0000000Z</c>).
    /// </summary>
    public static string StartedNotes(string triggerName, DateTimeOffset triggerTime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerName);
        return "trigger=" + triggerName + "; triggerTimeUtc=" + triggerTime.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>Log Batch Succeeded: <c>ObjectName</c> is the evidence file (<see cref="RunEvidenceFile.ObjectName"/>).</summary>
    public static LogBatchRunEventRequest Succeeded(Guid batchRunId, DateOnly businessDate, string evidenceObjectName, DateTime endedUtc) =>
        new(batchRunId, businessDate, PipelineNames.Master, "Succeeded", ObjectName: evidenceObjectName, EndedUtc: endedUtc);

    /// <summary>Log Failed At Extract / Card Settlement / Map / Reconciliation / Evidence.</summary>
    public static LogBatchRunEventRequest Failed(Guid batchRunId, DateOnly businessDate, StepFailure failure, DateTime endedUtc)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(batchRunId, businessDate, PipelineNames.Master, "Failed",
            StepName: failure.StepName, ErrorCode: failure.ErrorCode, ErrorMessage: failure.ErrorMessage, EndedUtc: endedUtc);
    }
}
