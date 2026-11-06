using System.Globalization;
using System.Text.RegularExpressions;

namespace CreditUnion.Batch.Core.Extract;

/// <summary>
/// Builds the statements of the Copy Db2 To Staging activity from an <see cref="ExtractTableConfig"/>.
/// ADF concatenates the configured names into SQL; the configuration table is writable by the schema
/// owner only, but the names are still checked as plain identifiers so a bad row cannot become an
/// injection or a malformed statement.
/// </summary>
public static partial class ExtractQueryBuilder
{
    /// <summary>
    /// <c>SELECT {columns} FROM {schema}.{table} WHERE {dateColumn} = DATE('{yyyy-MM-dd}')</c>, exactly the
    /// query of the ADF Db2 source (never <c>SELECT *</c>: a schema change must fail, not drift).
    /// </summary>
    public static string BuildSelect(ExtractTableConfig table, DateOnly businessDate)
    {
        ArgumentNullException.ThrowIfNull(table);
        var columns = ColumnList(table.SourceColumns, nameof(table.SourceColumns), table);
        Identifier(table.SourceSchema, nameof(table.SourceSchema), table);
        Identifier(table.SourceTable, nameof(table.SourceTable), table);
        Identifier(table.DateColumn, nameof(table.DateColumn), table);
        return string.Create(CultureInfo.InvariantCulture,
            $"SELECT {string.Join(", ", columns)} FROM {table.SourceSchema}.{table.SourceTable} WHERE {table.DateColumn} = DATE('{businessDate:yyyy-MM-dd}')");
    }

    /// <summary>The key columns of the table, in configuration order.</summary>
    public static IReadOnlyList<string> KeyColumns(ExtractTableConfig table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return ColumnList(table.KeyColumns, nameof(table.KeyColumns), table);
    }

    /// <summary>The pre-copy script of the ADF sink: <c>TRUNCATE TABLE {schema}.{table}</c> (T-SQL, bracket-quoted).</summary>
    public static string BuildTruncate(ExtractTableConfig table)
    {
        ArgumentNullException.ThrowIfNull(table);
        Identifier(table.TargetSchema, nameof(table.TargetSchema), table);
        Identifier(table.TargetTable, nameof(table.TargetTable), table);
        return $"TRUNCATE TABLE [{table.TargetSchema}].[{table.TargetTable}]";
    }

    private static string[] ColumnList(string value, string field, ExtractTableConfig table)
    {
        var columns = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (columns.Length == 0)
        {
            throw new ExtractConfigurationException($"ops.ExtractConfig '{table.SourceName}': {field} is empty.");
        }

        foreach (var column in columns)
        {
            Identifier(column, field, table);
        }

        return columns;
    }

    private static void Identifier(string value, string field, ExtractTableConfig table)
    {
        if (string.IsNullOrEmpty(value) || !IdentifierPattern().IsMatch(value))
        {
            throw new ExtractConfigurationException($"ops.ExtractConfig '{table.SourceName}': {field} contains a value that is not a plain identifier.");
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
