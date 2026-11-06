using System.Data;
using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.Core.Extract;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CreditUnion.Batch.KeyStone.Reporting;

/// <summary>
/// The reporting-store calls of PL_ExtractKeyStone, made with the cu_batch_writer login. Every call
/// opens its own connection (no pooled state to carry between activity attempts) and uses no command
/// timeout of its own: the activity's ADF timeout cancels it.
/// </summary>
public sealed class SqlExtractReportingStore(IOptions<ReportingSqlOptions> options) : IExtractReportingStore
{
    private const string LookupSql =
        "SELECT SourceName, SourceSchema, SourceTable, SourceColumns, KeyColumns, DateColumn, TargetSchema, TargetTable " +
        "FROM ops.ExtractConfig WHERE IsEnabled = 1 ORDER BY SortOrder";

    public async Task LogEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await OpenAsync(cancellationToken);
        await BatchRunLog.AppendAsync(connection, request, cancellationToken);
    }

    public async Task<IReadOnlyList<ExtractTableConfig>> LookupExtractConfigAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(LookupSql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tables = new List<ExtractTableConfig>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(new ExtractTableConfig(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        }

        return tables;
    }

    public async Task MergeKeyStoneAsync(DateOnly businessDate, Guid batchRunId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Procedure(connection, "core.usp_MergeKeyStone");
        command.Parameters.Add(new SqlParameter("@BusinessDate", SqlDbType.Date) { Value = businessDate.ToDateTime(TimeOnly.MinValue) });
        command.Parameters.Add(new SqlParameter("@BatchRunId", SqlDbType.UniqueIdentifier) { Value = batchRunId });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqlCommand Procedure(SqlConnection connection, string name) =>
        new(name, connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 0 };

    private static object OrNull<T>(T? value) => value is null ? DBNull.Value : value;

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
