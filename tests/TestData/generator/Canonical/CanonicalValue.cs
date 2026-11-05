using System.Data.SqlTypes;
using System.Globalization;

namespace CreditUnion.Batch.TestData.Canonical;

/// <summary>
/// Canonical text forms of values (ADR 0006). Golden files, input files, database snapshots and
/// checksums all use these forms, so two values are equal exactly when their canonical texts are
/// equal (ordinal). Formatting never rounds: a value that does not fit the column type throws.
/// </summary>
public static class CanonicalValue
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Exactly <paramref name="scale"/> fraction digits, '-' for negatives, no sign on zero.</summary>
    public static string Decimal(decimal value, int scale)
    {
        if (decimal.Round(value, scale, MidpointRounding.ToEven) != value)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Value has more than {scale} fraction digits.");
        }

        var text = value.ToString("F" + scale.ToString(Invariant), Invariant);
        return IsNegativeZero(text) ? text[1..] : text;
    }

    /// <summary>Exact form of a SQL decimal (up to 38 digits), padded to <paramref name="scale"/>.</summary>
    public static string Decimal(SqlDecimal value, int scale)
    {
        if (value.IsNull)
        {
            throw new ArgumentException("Null decimal has no canonical value; use null.", nameof(value));
        }

        if (value.Scale > scale)
        {
            var trimmed = SqlDecimal.AdjustScale(value, scale - value.Scale, fRound: false);
            if (trimmed != value)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value.ToString(), $"Value has more than {scale} fraction digits.");
            }

            value = trimmed;
        }
        else if (value.Scale < scale)
        {
            value = SqlDecimal.AdjustScale(value, scale - value.Scale, fRound: false);
        }

        var text = value.ToString();
        return IsNegativeZero(text) ? text[1..] : text;
    }

    public static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", Invariant);

    /// <summary><c>yyyy-MM-ddTHH:mm:ss</c> plus exactly <paramref name="precision"/> fraction digits (0-7).</summary>
    public static string Timestamp(DateTime value, int precision)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(precision, 7);
        var fractionTicks = value.Ticks % TimeSpan.TicksPerSecond;
        var fraction = fractionTicks.ToString("D7", Invariant);
        if (fraction[precision..].Any(c => c != '0'))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value.ToString("O", Invariant), $"Value has more than {precision} fraction digits.");
        }

        var whole = value.ToString("yyyy-MM-dd'T'HH:mm:ss", Invariant);
        return precision == 0 ? whole : whole + "." + fraction[..precision];
    }

    public static string Integer(long value) => value.ToString(Invariant);

    public static string Bit(bool value) => value ? "true" : "false";

    /// <summary>A CHAR(n) value as the database stores it: right-padded with spaces to n.</summary>
    public static string Char(string value, int length)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > length)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Value is longer than CHAR({length}).");
        }

        return value.PadRight(length);
    }

    public static decimal ParseDecimal(string text) =>
        decimal.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Invariant);

    public static SqlDecimal ParseSqlDecimal(string text) => SqlDecimal.Parse(text);

    public static DateOnly ParseDate(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", Invariant);

    public static DateTime ParseTimestamp(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var whole = DateTime.ParseExact(dot < 0 ? text : text[..dot], "yyyy-MM-dd'T'HH:mm:ss", Invariant, DateTimeStyles.None);
        if (dot < 0)
        {
            return whole;
        }

        var fraction = text[(dot + 1)..].PadRight(7, '0');
        return whole.AddTicks(long.Parse(fraction, NumberStyles.None, Invariant));
    }

    /// <summary>
    /// Checks that <paramref name="text"/> is the canonical form for <paramref name="type"/>
    /// (used to validate hand-written and generated files). Returns an error message or null.
    /// </summary>
    public static string? Validate(string? text, ColumnDefinition column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (text is null)
        {
            return column.Nullable ? null : $"{column.Name} is NOT NULL but the value is null.";
        }

        try
        {
            var type = column.Type;
            var canonical = type.Kind switch
            {
                SqlKind.Char => text.Length == type.Length ? text : null,
                SqlKind.VarChar or SqlKind.NVarChar => text.Length <= type.Length ? text : null,
                SqlKind.Decimal => Decimal(ParseSqlDecimal(text), type.Scale),
                SqlKind.Date => Date(ParseDate(text)),
                SqlKind.DateTime2 => Timestamp(ParseTimestamp(text), type.Precision),
                SqlKind.BigInt or SqlKind.Int => Integer(long.Parse(text, NumberStyles.AllowLeadingSign, Invariant)),
                SqlKind.Bit => text is "true" or "false" ? text : null,
                _ => null,
            };

            if (type.Kind == SqlKind.Decimal && DigitCount(canonical!) > type.Precision)
            {
                return $"{column.Name} value '{text}' exceeds {type}.";
            }

            return canonical == text ? null : $"{column.Name} value '{text}' is not canonical for {type}.";
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            return $"{column.Name} value '{text}' is not a valid {column.Type}: {ex.Message}";
        }
    }

    private static int DigitCount(string text) => text.Count(char.IsAsciiDigit);

    private static bool IsNegativeZero(string text) => text.StartsWith('-') && text.Skip(1).All(c => c is '0' or '.');
}
