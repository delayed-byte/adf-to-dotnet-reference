using System.Data;
using CreditUnion.Batch.Core.Card;
using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.KeyStone.Reporting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CreditUnion.Batch.KeyStone.Card;

/// <summary>
/// The reporting-store calls of PL_IngestCardSettlement, made with the cu_batch_writer login. Every call opens
/// its own connection (nothing carries between activity attempts) and sets no command timeout of its own: the
/// activity's ADF timeout cancels it. SqlClient connection retries are off (<see cref="Connections"/>, ADR 0009).
/// </summary>
public sealed class SqlCardSettlementStore(IOptions<ReportingSqlOptions> options) : ICardSettlementStore
{
    /// <summary>The Copy sink's <c>writeBatchSize</c>.</summary>
    internal const int WriteBatchSize = 10_000;

    private const string RawTable = "stg.CardSettlementRaw";

    private static readonly string[] StagingColumns = ["BusinessDate", "SourceFile", "RecordType", "Field01", "Field02", "Field03", "Field04", "Field05", "Field06", "Field07", "Field08", "Field09"];

    private static readonly string[] RawColumns =
        ["RecordType", "Field01", "Field02", "Field03", "Field04", "Field05", "Field06", "Field07", "Field08", "Field09"];

    public async Task LogBatchRunEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await OpenAsync(cancellationToken);
        await BatchRunLog.AppendAsync(connection, request, cancellationToken);
    }

    public async Task ResetStagingAsync(DateOnly businessDate, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Procedure(connection, "stg.usp_ResetCardSettlementStaging");
        command.Parameters.Add(Date("@BusinessDate", businessDate));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// The pre-copy script (<c>DELETE … WHERE BusinessDate AND SourceFile</c>, here with parameters instead of a
    /// concatenated string) and the bulk insert run in one transaction, so a failed attempt leaves no partial
    /// file behind and a retry cannot double the rows. Records are written in batches of
    /// <see cref="WriteBatchSize"/>; only one batch is held in memory.
    /// </summary>
    public async Task<long> LoadRawAsync(DateOnly businessDate, string sourceFile, IAsyncEnumerable<string?[]> records, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceFile);
        ArgumentNullException.ThrowIfNull(records);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var delete = new SqlCommand("DELETE FROM stg.CardSettlementRaw WHERE BusinessDate = @BusinessDate AND SourceFile = @SourceFile", connection, transaction) { CommandTimeout = 0 })
        {
            delete.Parameters.Add(Date("@BusinessDate", businessDate));
            delete.Parameters.Add(new SqlParameter("@SourceFile", SqlDbType.NVarChar, 400) { Value = sourceFile });
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
        {
            DestinationTableName = RawTable,
            BatchSize = WriteBatchSize,
            BulkCopyTimeout = 0,
        };
        foreach (var name in StagingColumns)
        {
            bulk.ColumnMappings.Add(name, name);
        }

        var date = businessDate.ToDateTime(TimeOnly.MinValue);
        using var batch = NewBatch();
        var total = 0L;
        await foreach (var record in records.WithCancellation(cancellationToken))
        {
            var row = batch.NewRow();
            row[0] = date;
            row[1] = sourceFile;
            for (var i = 0; i < RawColumns.Length; i++)
            {
                row[i + 2] = (object?)record[i] ?? DBNull.Value;
            }

            batch.Rows.Add(row);
            total++;
            if (batch.Rows.Count >= WriteBatchSize)
            {
                await bulk.WriteToServerAsync(batch, cancellationToken);
                batch.Clear();
            }
        }

        if (batch.Rows.Count > 0)
        {
            await bulk.WriteToServerAsync(batch, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return total;
    }

    public async Task<SettlementValidation> ValidateFileAsync(DateOnly businessDate, string sourceFile, Guid batchRunId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Procedure(connection, "stg.usp_ValidateCardSettlementFile");
        command.Parameters.Add(Date("@BusinessDate", businessDate));
        command.Parameters.Add(SourceFile(sourceFile));
        command.Parameters.Add(new SqlParameter("@BatchRunId", SqlDbType.UniqueIdentifier) { Value = batchRunId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        // Lookup with firstRowOnly: the first row; a missing row would fail the ADF expression too.
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("stg.usp_ValidateCardSettlementFile returned no row.");
        }

        return new SettlementValidation(
            reader.GetString(reader.GetOrdinal("SourceFile")),
            reader.GetBoolean(reader.GetOrdinal("IsValid")),
            NullableInt(reader, "DetailRowCount"),
            NullableInt(reader, "TrailerRowCount"),
            NullableDecimal(reader, "DetailTotalAmount"),
            NullableDecimal(reader, "TrailerTotalAmount"),
            reader.GetInt32(reader.GetOrdinal("ErrorCount")),
            NullableString(reader, "ErrorSummary"));
    }

    public async Task PromoteFileAsync(DateOnly businessDate, string sourceFile, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Procedure(connection, "stg.usp_PromoteCardSettlementFile");
        command.Parameters.Add(Date("@BusinessDate", businessDate));
        command.Parameters.Add(SourceFile(sourceFile));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CardValidationErrorRow>> GetValidationErrorsAsync(DateOnly businessDate, string sourceFile, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Procedure(connection, "stg.usp_GetCardSettlementValidationErrors");
        command.Parameters.Add(Date("@BusinessDate", businessDate));
        command.Parameters.Add(SourceFile(sourceFile));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CardValidationErrorRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CardValidationErrorRow(
                reader.GetString(reader.GetOrdinal("businessDate")),
                reader.GetString(reader.GetOrdinal("sourceFile")),
                reader.GetString(reader.GetOrdinal("batchRunId")),
                NullableString(reader, "recordType"),
                NullableString(reader, "processorRefNbr"),
                reader.GetString(reader.GetOrdinal("errorCode")),
                reader.GetString(reader.GetOrdinal("errorMessage"))));
        }

        return rows;
    }

    public async Task LogCardFileAsync(LogCardFileCall entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Procedure(connection, "ops.usp_LogCardFile");
        command.Parameters.Add(new SqlParameter("@BatchRunId", SqlDbType.UniqueIdentifier) { Value = entry.BatchRunId });
        command.Parameters.Add(Date("@BusinessDate", entry.BusinessDate));
        command.Parameters.Add(SourceFile(entry.SourceFile));
        command.Parameters.Add(new SqlParameter("@Outcome", SqlDbType.VarChar, 20) { Value = entry.Outcome });
        command.Parameters.Add(new SqlParameter("@DetailRowCount", SqlDbType.Int) { Value = OrNull(entry.DetailRowCount) });
        command.Parameters.Add(new SqlParameter("@TrailerRowCount", SqlDbType.Int) { Value = OrNull(entry.TrailerRowCount) });
        command.Parameters.Add(new SqlParameter("@DetailTotalAmount", SqlDbType.Decimal) { Precision = 19, Scale = 2, Value = OrNull(entry.DetailTotalAmount) });
        command.Parameters.Add(new SqlParameter("@TrailerTotalAmount", SqlDbType.Decimal) { Precision = 19, Scale = 2, Value = OrNull(entry.TrailerTotalAmount) });
        command.Parameters.Add(new SqlParameter("@ErrorSummary", SqlDbType.NVarChar, 2000) { Value = OrNull(entry.ErrorSummary) });
        command.Parameters.Add(new SqlParameter("@DestinationPath", SqlDbType.NVarChar, 1000) { Value = entry.DestinationPath });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MergeAsync(DateOnly businessDate, Guid batchRunId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Procedure(connection, "core.usp_MergeCardSettlement");
        command.Parameters.Add(Date("@BusinessDate", businessDate));
        command.Parameters.Add(new SqlParameter("@BatchRunId", SqlDbType.UniqueIdentifier) { Value = batchRunId });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DataTable NewBatch()
    {
        var table = new DataTable();
        table.Columns.Add("BusinessDate", typeof(DateTime));
        table.Columns.Add("SourceFile", typeof(string));
        foreach (var name in RawColumns)
        {
            table.Columns.Add(name, typeof(string));
        }

        return table;
    }

    private static SqlCommand Procedure(SqlConnection connection, string name) =>
        new(name, connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 0 };

    private static SqlParameter Date(string name, DateOnly value) =>
        new(name, SqlDbType.Date) { Value = value.ToDateTime(TimeOnly.MinValue) };

    private static SqlParameter SourceFile(string value) => new("@SourceFile", SqlDbType.NVarChar, 400) { Value = value };

    private static object OrNull<T>(T? value) => value is null ? DBNull.Value : value;

    private static int? NullableInt(SqlDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetInt32(reader.GetOrdinal(column));

    private static decimal? NullableDecimal(SqlDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetDecimal(reader.GetOrdinal(column));

    private static string? NullableString(SqlDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetString(reader.GetOrdinal(column));

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = Connections.CreateReportingConnection(options.Value);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
