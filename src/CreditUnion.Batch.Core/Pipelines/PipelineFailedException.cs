using System.Diagnostics.CodeAnalysis;

namespace CreditUnion.Batch.Core.Pipelines;

/// <summary>Error codes the pipelines raise (ADF Fail activities).</summary>
public static class PipelineErrorCodes
{
    /// <summary>PL_Master_NightlyBatch: a child pipeline or the evidence export failed.</summary>
    public const string BatchStepFailed = "BATCH_STEP_FAILED";

    /// <summary>PL_MapAndAggregate: a transaction code posted on the business date has no GL mapping.</summary>
    public const string TxnCodeUnmapped = "TXN_CODE_UNMAPPED";
}

/// <summary>
/// The .NET form of an ADF Fail activity: a pipeline run fails with an error code and a message.
/// <para>
/// Durable Functions keeps only an exception's type name and message when an orchestration fails
/// (TaskFailureDetails), so the code travels inside the message as <c>"{ErrorCode}: {detail}"</c>.
/// Callers that see only the failure details recover it with <see cref="TryParse"/> (ADR 0008).
/// </para>
/// </summary>
public sealed class PipelineFailedException : Exception
{
    private const string Separator = ": ";
    private const string Unspecified = "UNSPECIFIED";

    public PipelineFailedException()
        : this(Unspecified, "The pipeline failed.")
    {
    }

    public PipelineFailedException(string message)
        : this(Unspecified, message)
    {
    }

    public PipelineFailedException(string message, Exception innerException)
        : this(Unspecified, message, innerException)
    {
    }

    public PipelineFailedException(string errorCode, string detail, Exception? innerException = null)
        : base(Format(errorCode, detail), innerException)
    {
        ErrorCode = errorCode;
        Detail = detail;
    }

    public string ErrorCode { get; }

    public string Detail { get; }

    /// <summary>Reads the error code from a failure's type name and message, as Durable Functions reports them.</summary>
    public static bool TryParse(string? errorType, string? message, [NotNullWhen(true)] out string? errorCode, [NotNullWhen(true)] out string? detail)
    {
        errorCode = null;
        detail = null;
        if (!string.Equals(errorType, typeof(PipelineFailedException).FullName, StringComparison.Ordinal) || message is null)
        {
            return false;
        }

        var separator = message.IndexOf(Separator, StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        errorCode = message[..separator];
        detail = message[(separator + Separator.Length)..];
        return true;
    }

    private static string Format(string errorCode, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentNullException.ThrowIfNull(detail);
        if (errorCode.Contains(Separator, StringComparison.Ordinal))
        {
            throw new ArgumentException("An error code cannot contain ': '.", nameof(errorCode));
        }

        return errorCode + Separator + detail;
    }
}
