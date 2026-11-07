using CreditUnion.Batch.Core.Card;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Map;
using CreditUnion.Batch.Core.Master;
using CreditUnion.Batch.Core.Reconciliation;
using Microsoft.DurableTask;

namespace CreditUnion.Batch.Functions.Orchestrators;

/// <summary>Turns an ADF activity policy into the Durable options of the call that implements the activity (ADR 0008).</summary>
public static class AdfRetry
{
    /// <summary>
    /// <c>retry + 1</c> attempts, a fixed interval (backoff coefficient 1). Failures that retrying cannot
    /// fix are not retried: <see cref="ExtractDataException"/> (ADR 0005), <see cref="ExtractConfigurationException"/>,
    /// <see cref="CardFileDataException"/> (ADR 0010), and the Map failures that are not transient (ADR 0011, ADR 0012).
    /// </summary>
    public static TaskOptions Options(ActivityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return TaskOptions.FromRetryPolicy(new RetryPolicy(policy.MaxNumberOfAttempts, policy.RetryInterval, backoffCoefficient: 1.0, maxRetryInterval: policy.RetryInterval)
        {
            HandleFailure = IsRetriable,
        });
    }

    /// <summary>Whether retrying can help: false for data, configuration and rejected-request errors, true for everything else (timeouts, connection failures, 5xx).</summary>
    public static bool IsRetriable(TaskFailureDetails failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return !failure.IsCausedBy<ExtractDataException>() && !failure.IsCausedBy<ExtractConfigurationException>()
            && !failure.IsCausedBy<CardFileDataException>()
            && !failure.IsCausedBy<KeyBridgeRejectedException>() && !failure.IsCausedBy<KeyBridgeResponseException>()
            && !failure.IsCausedBy<KeyBridgeConfigurationException>() && !failure.IsCausedBy<GlMappingInvalidException>()
            && !failure.IsCausedBy<GlAggregationException>()
            && !failure.IsCausedBy<ReconAlertRejectedException>() && !failure.IsCausedBy<ReconAlertConfigurationException>()
            && !failure.IsCausedBy<ReconInputException>()
            && !failure.IsCausedBy<RunEvidenceInputException>();
    }
}
