using System.Data;
using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.Core.Extract;
using IBM.Data.Db2;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CreditUnion.Batch.KeyStone.Staging;

/// <summary>
/// The Copy Db2 To Staging activity: truncates the staging table, then streams
/// <c>DB2DataReader</c> through <see cref="Db2StagingReader"/> into <see cref="SqlBulkCopy"/> in batches
/// of 10,000 with a table lock (the ADF sink's <c>writeBatchSize</c> and <c>sqlWriterUseTableLock</c>).
/// No intermediate file is written and no more than one batch of rows is held in memory.
/// <para>
/// The Db2 query runs first and the table is truncated only once it has returned, so a refused
/// connection or a failing query leaves the previous staging rows alone. There is no retry here: a failed
/// attempt throws and the orchestrator's RetryPolicy runs the whole activity again, which truncates again.
/// </para>
/// </summary>
public sealed class Db2StagingCopier(IOptions<KeyStoneDb2Options> keyStone, IOptions<ReportingSqlOptions> reporting) : IKeyStoneStagingCopier
{
    /// <summary>ADF sink <c>writeBatchSize</c>.</summary>
    public const int WriteBatchSize = 10_000;

    public async Task<long> CopyToStagingAsync(ExtractTableConfig table, DateOnly businessDate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        var select = ExtractQueryBuilder.BuildSelect(table, businessDate);
        var truncate = ExtractQueryBuilder.BuildTruncate(table);
        var keys = ExtractQueryBuilder.KeyColumns(table);

        await using var target = Connections.CreateReportingConnection(reporting.Value);
        await target.OpenAsync(cancellationToken);
        var columns = await ReadStagingColumnsAsync(target, table, cancellationToken);

        await using var source = Connections.CreateKeyStoneConnection(keyStone.Value);
        await source.OpenAsync(cancellationToken);
        await using var query = new DB2Command(select, source) { CommandTimeout = 0 };
        await using var db2Reader = (DB2DataReader)await query.ExecuteReaderAsync(cancellationToken);
        await using var staging = new Db2StagingReader(db2Reader, columns, table.TargetName, keys);

        await using (var clear = new SqlCommand(truncate, target) { CommandTimeout = 0 })
        {
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        using var bulk = new SqlBulkCopy(target, SqlBulkCopyOptions.TableLock, null)
        {
            DestinationTableName = $"[{table.TargetSchema}].[{table.TargetTable}]",
            BatchSize = WriteBatchSize,
            BulkCopyTimeout = 0,
            EnableStreaming = true,
        };
        for (var i = 0; i < columns.Count; i++)
        {
            bulk.ColumnMappings.Add(i, columns[i].Name);
        }

        try
        {
            await bulk.WriteToServerAsync(staging, cancellationToken);
        }
        catch (Exception) when (staging.DataError is not null)
        {
            // SqlBulkCopy may wrap what the reader threw; the data error is what the caller (and the retry policy) must see.
            throw staging.DataError;
        }

        return staging.RowsRead;
    }

    private static async Task<IReadOnlyList<StagingColumn>> ReadStagingColumnsAsync(SqlConnection connection, ExtractTableConfig table, CancellationToken cancellationToken)
    {
        // Validates identifiers, so the names below are plain identifiers.
        var select = ExtractQueryBuilder.BuildSelect(table, default);
        var columnList = select["SELECT ".Length..select.IndexOf(" FROM ", StringComparison.Ordinal)];
        await using var command = new SqlCommand($"SELECT {columnList} FROM [{table.TargetSchema}].[{table.TargetTable}] WHERE 1 = 0", connection) { CommandTimeout = 0 };
        SqlDataReader reader;
        try
        {
            reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, cancellationToken);
        }
        catch (SqlException ex) when (ex.Number is 207 or 208)
        {
            throw new ExtractConfigurationException(
                $"{table.TargetName}: the staging table is missing or lacks a column of ops.ExtractConfig '{table.SourceName}' (SQL error {ex.Number}).", ex);
        }

        await using (reader)
        {
            var schema = reader.GetColumnSchema();
            return [.. schema.Select(c => new StagingColumn(
                c.ColumnName,
                KindOf(c.DataTypeName, table, c.ColumnName),
                c.ColumnSize ?? 0,
                c.NumericPrecision ?? 0,
                c.NumericScale ?? 0))];
        }
    }

    private static StagingKind KindOf(string? dataTypeName, ExtractTableConfig table, string column) =>
        dataTypeName?.ToLowerInvariant() switch
        {
            "char" => StagingKind.FixedChar,
            "varchar" => StagingKind.VarChar,
            "decimal" or "numeric" => StagingKind.ExactDecimal,
            "date" => StagingKind.Date,
            "datetime2" => StagingKind.DateTime2,
            "bigint" => StagingKind.BigInt,
            "int" => StagingKind.Integer32,
            _ => throw new ExtractConfigurationException($"{table.TargetName}.{column}: staging column type '{dataTypeName}' has no read rule in ADR 0005."),
        };
}
