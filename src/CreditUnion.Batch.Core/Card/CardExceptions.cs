namespace CreditUnion.Batch.Core.Card;

/// <summary>
/// A settlement file cannot be landed: a quoted field that never closes, or a field longer than its
/// staging column. ADF's Copy activity fails on these too. Retrying the same bytes cannot help, so the
/// retry policy skips it (ADR 0010). The message names the file, the record and the column, never a value.
/// </summary>
public sealed class CardFileDataException : Exception
{
    public CardFileDataException()
        : base("The settlement file cannot be loaded.")
    {
    }

    public CardFileDataException(string message)
        : base(message)
    {
    }

    public CardFileDataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
