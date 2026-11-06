using System.Data.SqlTypes;
using System.Text;

namespace CreditUnion.Batch.KeyStone.Staging;

/// <summary>
/// The value rules of ADR 0005 that do not need a database: what makes a Db2 value copyable exactly
/// into its staging column. Each method returns the value to hand to SqlBulkCopy or reports why it
/// cannot be copied (as a <see cref="StagingValueException"/> naming only the rule, never the value).
/// </summary>
public static class StagingValueRules
{
    /// <summary>
    /// CHAR and VARCHAR: verbatim, padding kept. CHAR columns hold ASCII codes and identifiers, so a
    /// non-ASCII CHAR value fails the copy; VARCHAR keeps any character (the free-text columns use a
    /// UTF-8 collation, migration 0007). The value must fit the column (bytes for VARCHAR, as Db2 counts).
    /// </summary>
    public static string Text(string value, StagingColumn column)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(column);
        if (column.Kind == StagingKind.FixedChar)
        {
            if (!Ascii.IsValid(value))
            {
                throw new StagingValueException("a CHAR value contains non-ASCII characters");
            }

            if (value.Length > column.Length)
            {
                throw new StagingValueException($"a CHAR value is longer than the staging column ({column.Length})");
            }

            return value;
        }

        if (value.Length > column.Length || Encoding.UTF8.GetByteCount(value) > column.Length)
        {
            throw new StagingValueException($"a VARCHAR value is longer than the staging column ({column.Length} bytes)");
        }

        return value;
    }

    /// <summary>
    /// DECIMAL: parsed from the exact text of <c>DB2Decimal</c> (never through System.Decimal or double).
    /// A value with more fraction digits than the staging column is a scale mismatch, not something to
    /// round; fewer digits are widened exactly.
    /// </summary>
    public static SqlDecimal ExactDecimal(string exactText, StagingColumn column)
    {
        ArgumentNullException.ThrowIfNull(exactText);
        ArgumentNullException.ThrowIfNull(column);
        SqlDecimal parsed;
        try
        {
            parsed = SqlDecimal.Parse(exactText);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new StagingValueException("a DECIMAL value could not be read exactly");
        }

        if (parsed.Precision - parsed.Scale > column.Precision - column.Scale)
        {
            throw new StagingValueException($"a DECIMAL value does not fit decimal({column.Precision},{column.Scale})");
        }

        if (parsed.Scale > column.Scale)
        {
            // A trailing-zero fraction such as 1.250 into scale 2 is the same number, but the source column's
            // scale is then larger than the staging column's, which ADR 0005 calls a configuration error.
            throw new StagingValueException($"a DECIMAL value has scale {parsed.Scale} but the staging column has scale {column.Scale}");
        }

        try
        {
            return SqlDecimal.ConvertToPrecScale(parsed, column.Precision, column.Scale);
        }
        catch (Exception ex) when (ex is OverflowException or SqlTypeException)
        {
            throw new StagingValueException($"a DECIMAL value does not fit decimal({column.Precision},{column.Scale})");
        }
    }

    /// <summary>DATE: midnight, no time zone. A DATE read with a time of day is not a date.</summary>
    public static DateTime Date(DateTime value)
    {
        if (value.TimeOfDay != TimeSpan.Zero)
        {
            throw new StagingValueException("a DATE value has a time of day");
        }

        return DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
    }

    /// <summary>TIMESTAMP: KeyStone local wall-clock time, copied unchanged (never converted to UTC).</summary>
    public static DateTime Timestamp(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
}

/// <summary>A Db2 value that cannot be copied exactly. The message states the rule that failed, never the value.</summary>
public sealed class StagingValueException : Exception
{
    public StagingValueException()
        : base("A value cannot be copied exactly.")
    {
    }

    public StagingValueException(string message)
        : base(message)
    {
    }

    public StagingValueException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
