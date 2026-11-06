namespace CreditUnion.Batch.Core.Map;

// PL_MapAndAggregate failures (ADR 0011, ADR 0012). Messages name status codes, transaction codes and setting
// names; never a URL, a header, a credential or a response body. Only KeyBridgeUnavailableException (and
// TimeoutException) is transient: AdfRetry skips the others, because the same request or data fails the same way.

/// <summary>KeyBridge could not be reached, or answered 408, 429 or 5xx. Retried by the Web activity policy.</summary>
public sealed class KeyBridgeUnavailableException : Exception
{
    public KeyBridgeUnavailableException()
        : base("KeyBridge is unavailable.")
    {
    }

    public KeyBridgeUnavailableException(string message)
        : base(message)
    {
    }

    public KeyBridgeUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>KeyBridge refused the request (any other non-2xx status, e.g. 401, 403, 404, or a redirect). Not retried.</summary>
public sealed class KeyBridgeRejectedException : Exception
{
    public KeyBridgeRejectedException()
        : base("KeyBridge rejected the request.")
    {
    }

    public KeyBridgeRejectedException(string message)
        : base(message)
    {
    }

    public KeyBridgeRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>KeyBridge answered 2xx with a body the pipeline cannot use (not JSON, no <c>mappings</c>, over 4 MB). Not retried.</summary>
public sealed class KeyBridgeResponseException : Exception
{
    public KeyBridgeResponseException()
        : base("The KeyBridge response cannot be used.")
    {
    }

    public KeyBridgeResponseException(string message)
        : base(message)
    {
    }

    public KeyBridgeResponseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The <c>KeyBridge</c> settings are missing or unsafe (no base URL, credentials in the URL, no password). Not retried.</summary>
public sealed class KeyBridgeConfigurationException : Exception
{
    public KeyBridgeConfigurationException()
        : base("The KeyBridge settings are invalid.")
    {
    }

    public KeyBridgeConfigurationException(string message)
        : base(message)
    {
    }

    public KeyBridgeConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// <c>ref.usp_UpsertTxnCodeGlMap</c> rejected the mapping (<c>KEYBRIDGE_MAPPING_INVALID</c>, errors 53000 to 53004); the
/// cache is unchanged. Not retried: the same JSON fails the same way. The message is the procedure's, which names no value.
/// </summary>
public sealed class GlMappingInvalidException : Exception
{
    public GlMappingInvalidException()
        : base("KEYBRIDGE_MAPPING_INVALID: the GL mapping was rejected.")
    {
    }

    public GlMappingInvalidException(string message)
        : base(message)
    {
    }

    public GlMappingInvalidException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The data flow's inner join found transactions without a mapping row, which the unmapped-code check should have
/// prevented (ADR 0011). Nothing is written. Not retried. The message names transaction codes only.
/// </summary>
public sealed class GlAggregationException : Exception
{
    public GlAggregationException()
        : base("DailyGlTotals cannot be aggregated.")
    {
    }

    public GlAggregationException(string message)
        : base(message)
    {
    }

    public GlAggregationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
