using System.Collections;
using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Logging;
using IBM.Data.Db2;

namespace CreditUnion.Batch.KeyStone.Staging;

/// <summary>
/// Presents a <see cref="DB2DataReader"/> to <c>SqlBulkCopy</c> as the values ADR 0005 prescribes, one row
/// at a time, so the copy streams with no intermediate file: DECIMAL as <see cref="SqlDecimal"/> read
/// through <c>GetDB2Decimal</c> (never <c>GetDecimal</c>, which rounds beyond System.Decimal), CHAR and
/// VARCHAR verbatim, DATE and TIMESTAMP as the naive <see cref="DateTime"/> Db2 returns, NULL as NULL.
/// The columns are the staging table's, in the order of the SELECT.
/// <para>
/// A value that cannot be copied exactly (hour-24 timestamp, non-ASCII CHAR, scale larger than the
/// staging column) ends the read with an <see cref="ExtractDataException"/> naming the table, the row
/// number, the key (masked to its last four characters) and the column, never the value.
/// </para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1010:Collections should implement generic interface", Justification = "DbDataReader is an IEnumerable by contract; SqlBulkCopy never enumerates it.")]
public sealed class Db2StagingReader : DbDataReader
{
    private readonly DB2DataReader source;
    private readonly IReadOnlyList<StagingColumn> columns;
    private readonly string tableName;
    private readonly (string Name, int Ordinal)[] keys;
    private long rowNumber;

    public Db2StagingReader(DB2DataReader source, IReadOnlyList<StagingColumn> columns, string tableName, IReadOnlyList<string> keyColumns)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(keyColumns);
        this.source = source;
        this.columns = columns;
        this.tableName = tableName;
        if (source.FieldCount != columns.Count)
        {
            throw new ExtractConfigurationException(
                $"{tableName}: the Db2 query returns {source.FieldCount} column(s) but the staging table has {columns.Count}.");
        }

        keys = [.. keyColumns.Select(k => (k, Ordinal: IndexOf(columns, k))).Where(k => k.Ordinal >= 0)];
    }

    public long RowsRead => rowNumber;

    /// <summary>The first value the reader refused to hand over; set before the exception is thrown so a caller can recover it if SqlBulkCopy wraps it.</summary>
    public ExtractDataException? DataError { get; private set; }

    public override int FieldCount => columns.Count;

    public override bool HasRows => source.HasRows;

    public override bool IsClosed => source.IsClosed;

    public override int Depth => 0;

    public override int RecordsAffected => -1;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        var more = source.Read();
        if (more)
        {
            rowNumber++;
        }

        return more;
    }

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        var more = await source.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (more)
        {
            rowNumber++;
        }

        return more;
    }

    public override bool NextResult() => false;

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    public override bool IsDBNull(int ordinal) => source.IsDBNull(ordinal);

    public override string GetName(int ordinal) => columns[ordinal].Name;

    public override int GetOrdinal(string name) => IndexOf(columns, name) is var i and >= 0 ? i : throw new ArgumentException($"No column named {name}.", nameof(name));

    public override string GetDataTypeName(int ordinal) => columns[ordinal].Kind.ToString();

    public override Type GetFieldType(int ordinal) => columns[ordinal].Kind switch
    {
        StagingKind.FixedChar or StagingKind.VarChar => typeof(string),
        StagingKind.ExactDecimal => typeof(SqlDecimal),
        StagingKind.Date or StagingKind.DateTime2 => typeof(DateTime),
        StagingKind.BigInt => typeof(long),
        StagingKind.Integer32 => typeof(int),
        _ => typeof(object),
    };

    public override object GetValue(int ordinal)
    {
        if (source.IsDBNull(ordinal))
        {
            return DBNull.Value;
        }

        var column = columns[ordinal];
        try
        {
            return column.Kind switch
            {
                StagingKind.FixedChar or StagingKind.VarChar => StagingValueRules.Text(source.GetString(ordinal), column),
                StagingKind.ExactDecimal => StagingValueRules.ExactDecimal(source.GetDB2Decimal(ordinal).ToString(), column),
                StagingKind.Date => StagingValueRules.Date(source.GetDateTime(ordinal)),
                StagingKind.DateTime2 => StagingValueRules.Timestamp(source.GetDateTime(ordinal)),
                StagingKind.BigInt => source.GetInt64(ordinal),
                StagingKind.Integer32 => source.GetInt32(ordinal),
                _ => throw new NotSupportedException($"No read rule for {column.Kind}."),
            };
        }
        catch (StagingValueException ex)
        {
            throw DataErrorFor(column, ex.Message);
        }
        catch (InvalidCastException)
        {
            // ADR 0005 #5: a TIMESTAMP at hour 24 (2026-11-04-24.00.00) cannot be read as a DateTime.
            throw DataErrorFor(column, "the value cannot be read as the staging column type (a TIMESTAMP at hour 24, for example)");
        }
        catch (OverflowException)
        {
            throw DataErrorFor(column, "the value is outside the range of the staging column type");
        }
    }

    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);

    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);

    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);

    public override decimal GetDecimal(int ordinal) =>
        throw new NotSupportedException("Decimals are read as SqlDecimal (ADR 0005); System.Decimal would round DECIMAL(31,8) values.");

    public override bool GetBoolean(int ordinal) => throw new NotSupportedException();

    public override byte GetByte(int ordinal) => throw new NotSupportedException();

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

    public override char GetChar(int ordinal) => throw new NotSupportedException();

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

    public override double GetDouble(int ordinal) => throw new NotSupportedException("Money is never read as double.");

    public override float GetFloat(int ordinal) => throw new NotSupportedException();

    public override Guid GetGuid(int ordinal) => throw new NotSupportedException();

    public override short GetInt16(int ordinal) => throw new NotSupportedException();

    public override IEnumerator GetEnumerator() => throw new NotSupportedException();

    public override DataTable GetSchemaTable() => throw new NotSupportedException();

    private static int IndexOf(IReadOnlyList<StagingColumn> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    // The driver's own exception is deliberately not attached: its message can quote the offending value.
    private ExtractDataException DataErrorFor(StagingColumn column, string reason) =>
        DataError = new ExtractDataException($"{tableName} row {rowNumber} (key {DescribeKey()}) column {column.Name}: {reason}. ADR 0005; the value is not logged.");

    // Keys are bigint or CHAR identifiers (TXN_ID, ACCOUNT_NBR, GL_ENTRY_ID); the number is masked like any member, account or card number.
    private string DescribeKey()
    {
        if (keys.Length == 0)
        {
            return "unknown";
        }

        var parts = new List<string>(keys.Length);
        foreach (var (name, ordinal) in keys)
        {
            try
            {
                parts.Add($"{name}={(source.IsDBNull(ordinal) ? "NULL" : LogMask.LastFour(Convert.ToString(source.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture)?.Trim()))}");
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                parts.Add($"{name}=unreadable");
            }
        }

        return string.Join(", ", parts);
    }
}
