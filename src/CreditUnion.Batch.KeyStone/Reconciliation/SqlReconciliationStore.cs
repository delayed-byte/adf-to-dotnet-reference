using System.Data;
using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Reconciliation;
using CreditUnion.Batch.KeyStone.Reporting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CreditUnion.Batch.KeyStone.Reconciliation;

/// <summary>
/// The reporting-store calls of PL_Reconciliation, made with the cu_batch_writer login. Every call opens its own connection
/// (nothing carries between activity attempts) and sets no command timeout of its own: the activity's ADF timeout cancels
/// it. SqlClient connection retries are off (<see cref="Connections"/>, ADR 0009). The procedures hold the rules; this class
/// only calls them (ADR 0013). This phase reads no Db2 and no blob.
/// </summary>
public sealed class SqlReconciliationStore(IOptions<ReportingSqlOptions> options) : IReconciliationStore
{
    public async Task LogBatchRunEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await OpenAsync(cancellationToken);
        await BatchRunLog.AppendAsync(connection, request, cancellationToken);
    }

    public async Task<CardReconTotals> GetCardTotalsAsync(ReconQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Procedure(connection, "ops.usp_GetCardReconTotals");
        command.Parameters.Add(Date("@BusinessDate", query.BusinessDate));
        command.Parameters.Add(Money("@Tolerance", query.Tolerance));
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        var row = await FirstRowAsync(reader, "ops.usp_GetCardReconTotals", cancellationToken);
        return new CardReconTotals(
            row.GetDecimal(reader.GetOrdinal("SettlementTotal")),
            row.GetDecimal(reader.GetOrdinal("KeyStoneTotal")),
            row.GetDecimal(reader.GetOrdinal("Difference")),
            row.GetDecimal(reader.GetOrdinal("AbsDifference")),
            row.GetBoolean(reader.GetOrdinal("IsOutOfTolerance")));
    }

    public async Task<GlReconTotals> GetGlTotalsAsync(ReconQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Procedure(connection, "ops.usp_GetGlReconTotals");
        command.Parameters.Add(Date("@BusinessDate", query.BusinessDate));
        command.Parameters.Add(Money("@Tolerance", query.Tolerance));
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        var row = await FirstRowAsync(reader, "ops.usp_GetGlReconTotals", cancellationToken);
        var outOfTolerance = reader.GetOrdinal("ItemsOutOfTolerance");
        return new GlReconTotals(
            row.GetInt32(reader.GetOrdinal("ItemsCompared")),
            row.IsDBNull(outOfTolerance) ? 0 : row.GetInt32(outOfTolerance),
            row.GetDecimal(reader.GetOrdinal("TotalAbsDifference")),
            row.GetBoolean(reader.GetOrdinal("IsOutOfTolerance")));
    }

    public async Task<ReconExceptionResult> RecordExceptionsAsync(RecordReconExceptionsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Procedure(connection, "ops.usp_RecordReconExceptions");
        command.Parameters.Add(Date("@BusinessDate", request.BusinessDate));
        command.Parameters.Add(new SqlParameter("@BatchRunId", SqlDbType.UniqueIdentifier) { Value = request.BatchRunId });
        command.Parameters.Add(Money("@Tolerance", request.Tolerance));
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        var row = await FirstRowAsync(reader, "ops.usp_RecordReconExceptions", cancellationToken);
        return new ReconExceptionResult(
            row.GetBoolean(reader.GetOrdinal("IsOutOfTolerance")),
            row.GetInt32(reader.GetOrdinal("ExceptionCount")),
            row.GetInt32(reader.GetOrdinal("NewExceptionCount")),
            row.GetBoolean(reader.GetOrdinal("AlertRequired")),
            row.GetString(reader.GetOrdinal("AlertSummary")));
    }

    public async Task MarkAlertSentAsync(MarkReconAlertSentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Procedure(connection, "ops.usp_MarkReconAlertSent");
        command.Parameters.Add(Date("@BusinessDate", request.BusinessDate));
        command.Parameters.Add(new SqlParameter("@BatchRunId", SqlDbType.UniqueIdentifier) { Value = request.BatchRunId });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<SqlDataReader> FirstRowAsync(SqlDataReader reader, string procedure, CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException($"{procedure} returned no row; it always returns exactly one.");
        }

        return reader;
    }

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

    private static SqlCommand Procedure(SqlConnection connection, string name) =>
        new(name, connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 0 };

    private static SqlParameter Date(string name, DateOnly value) =>
        new(name, SqlDbType.Date) { Value = value.ToDateTime(TimeOnly.MinValue) };

    /// <summary>decimal(19,2), as the procedures declare it. The tolerance was checked to have at most two decimals, so nothing rounds.</summary>
    private static SqlParameter Money(string name, decimal value) =>
        new(name, SqlDbType.Decimal) { Precision = 19, Scale = 2, Value = value };
}
