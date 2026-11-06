namespace CreditUnion.Batch.Core.Extract;

/// <summary>
/// A value in the KeyStone data cannot be copied exactly (ADR 0005): a timestamp at hour 24, a non-ASCII
/// CHAR value, or a DECIMAL that does not fit the staging column. Retrying cannot help, so the extract's
/// retry policies skip it. The message names the table, the key and the column, never the value; keys
/// are masked to their last four characters.
/// </summary>
public sealed class ExtractDataException : Exception
{
    public ExtractDataException()
        : base("The KeyStone data cannot be copied exactly.")
    {
    }

    public ExtractDataException(string message)
        : base(message)
    {
    }

    public ExtractDataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The extract configuration or staging schema disagrees with the source (a bad identifier in
/// <c>ops.ExtractConfig</c>, a column count or decimal scale that does not match). A deployment
/// error: not retried.
/// </summary>
public sealed class ExtractConfigurationException : Exception
{
    public ExtractConfigurationException()
        : base("The KeyStone extract is misconfigured.")
    {
    }

    public ExtractConfigurationException(string message)
        : base(message)
    {
    }

    public ExtractConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
