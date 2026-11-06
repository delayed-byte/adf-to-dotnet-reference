using System.Data.SqlTypes;
using CreditUnion.Batch.TestData.Canonical;
using IBM.Data.Db2;
using IBM.Data.DB2Types;

namespace CreditUnion.Batch.TestHarness.Db2;

/// <summary>
/// Reads and writes Db2 values by the rules of ADR 0005, which were confirmed against
/// Net.IBM.Data.Db2 10.0 and Db2 12.1 (Db2TypeHandlingProbeTests pins them):
/// CHAR and VARCHAR are taken verbatim (no trimming); DECIMAL goes through <see cref="DB2Decimal"/>
/// because <c>GetDecimal</c>/<c>GetValue</c> silently round beyond 28 digits and string parameters are
/// converted through System.Decimal; DATE and TIMESTAMP are naive (DateTimeKind.Unspecified) and are
/// never shifted between time zones.
/// </summary>
public static class Db2Values
{
    public static string? ReadCanonical(DB2DataReader reader, int ordinal, ColumnType type)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(type);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return type.Kind switch
        {
            SqlKind.Char or SqlKind.VarChar or SqlKind.NVarChar => reader.GetString(ordinal),
            SqlKind.Decimal => CanonicalValue.Decimal(SqlDecimal.Parse(reader.GetDB2Decimal(ordinal).ToString()), type.Scale),
            SqlKind.Date => CanonicalValue.Date(DateOnly.FromDateTime(reader.GetDateTime(ordinal))),
            SqlKind.DateTime2 => CanonicalValue.Timestamp(reader.GetDateTime(ordinal), type.Precision),
            SqlKind.BigInt => CanonicalValue.Integer(reader.GetInt64(ordinal)),
            SqlKind.Int => CanonicalValue.Integer(reader.GetInt32(ordinal)),
            _ => throw new NotSupportedException($"No Db2 read rule for {type}."),
        };
    }

    /// <summary>
    /// The canonical text value as a Db2 SQL literal, for multi-row INSERT statements (large loads). Exact like
    /// <see cref="Bind"/>: decimals keep every digit, strings keep every space, timestamps keep six fraction digits.
    /// </summary>
    public static string ToLiteral(string? text, ColumnType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (text is null)
        {
            return "NULL";
        }

        switch (type.Kind)
        {
            case SqlKind.Char or SqlKind.VarChar or SqlKind.NVarChar:
                return "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
            case SqlKind.Decimal or SqlKind.BigInt or SqlKind.Int:
                return text.All(c => char.IsAsciiDigit(c) || c is '-' or '.') ? text : throw new FormatException($"'{text}' is not a numeric literal.");
            case SqlKind.Date:
                return "DATE('" + CanonicalValue.ParseDate(text).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "')";
            case SqlKind.DateTime2:
                return "TIMESTAMP('" + CanonicalValue.ParseTimestamp(text).ToString("yyyy-MM-dd-HH.mm.ss.ffffff", System.Globalization.CultureInfo.InvariantCulture) + "')";
            default:
                throw new NotSupportedException($"No Db2 literal rule for {type}.");
        }
    }

    /// <summary>Binds a canonical text value to a Db2 parameter with an explicit type, so nothing is inferred or rounded.</summary>
    public static void Bind(DB2Parameter parameter, string? text, ColumnType type)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(type);
        switch (type.Kind)
        {
            case SqlKind.Char:
                parameter.DB2Type = DB2Type.Char;
                parameter.Size = type.Length;
                parameter.Value = text ?? (object)DBNull.Value;
                break;
            case SqlKind.VarChar or SqlKind.NVarChar:
                parameter.DB2Type = DB2Type.VarChar;
                parameter.Value = text ?? (object)DBNull.Value;
                break;
            case SqlKind.Decimal:
                parameter.DB2Type = DB2Type.Decimal;
                parameter.Precision = (byte)type.Precision;
                parameter.Scale = (byte)type.Scale;
                parameter.Value = text is null ? DBNull.Value : DB2Decimal.Parse(text);
                break;
            case SqlKind.Date:
                parameter.DB2Type = DB2Type.Date;
                parameter.Value = text is null ? DBNull.Value : CanonicalValue.ParseDate(text).ToDateTime(TimeOnly.MinValue);
                break;
            case SqlKind.DateTime2:
                parameter.DB2Type = DB2Type.Timestamp;
                parameter.Value = text is null ? DBNull.Value : CanonicalValue.ParseTimestamp(text);
                break;
            case SqlKind.BigInt:
                parameter.DB2Type = DB2Type.BigInt;
                parameter.Value = text is null ? DBNull.Value : long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                break;
            case SqlKind.Int:
                parameter.DB2Type = DB2Type.Integer;
                parameter.Value = text is null ? DBNull.Value : int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                break;
            default:
                throw new NotSupportedException($"No Db2 bind rule for {type}.");
        }
    }
}
