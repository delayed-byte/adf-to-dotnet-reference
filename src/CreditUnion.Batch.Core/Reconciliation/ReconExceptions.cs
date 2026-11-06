namespace CreditUnion.Batch.Core.Reconciliation;

// PL_Reconciliation failures (ADR 0013). Messages name status codes and setting names; never the webhook URL (its
// path carries a secret token), a header or a response body. Only ReconAlertUnavailableException (and
// TimeoutException) is transient: AdfRetry skips the others, because the same request fails the same way.

/// <summary>The webhook could not be reached, or answered 408, 429 or 5xx. Retried by the Post Recon Alert policy.</summary>
public sealed class ReconAlertUnavailableException : Exception
{
    public ReconAlertUnavailableException()
        : base("The reconciliation alert webhook is unavailable.")
    {
    }

    public ReconAlertUnavailableException(string message)
        : base(message)
    {
    }

    public ReconAlertUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The webhook refused the request (any other non-2xx status, e.g. 400, 401, 404, or a redirect). Not retried.</summary>
public sealed class ReconAlertRejectedException : Exception
{
    public ReconAlertRejectedException()
        : base("The reconciliation alert webhook rejected the request.")
    {
    }

    public ReconAlertRejectedException(string message)
        : base(message)
    {
    }

    public ReconAlertRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The <c>ReconAlertWebhook</c> setting is missing or unusable. Not retried.</summary>
public sealed class ReconAlertConfigurationException : Exception
{
    public ReconAlertConfigurationException()
        : base("The ReconAlertWebhook settings are invalid.")
    {
    }

    public ReconAlertConfigurationException(string message)
        : base(message)
    {
    }

    public ReconAlertConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A pipeline parameter the procedures cannot honour exactly (a negative tolerance, or one with more than two decimals). Not retried.</summary>
public sealed class ReconInputException : Exception
{
    public ReconInputException()
        : base("The reconciliation input is invalid.")
    {
    }

    public ReconInputException(string message)
        : base(message)
    {
    }

    public ReconInputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
