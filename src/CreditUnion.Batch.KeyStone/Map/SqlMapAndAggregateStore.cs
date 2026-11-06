using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Map;
using CreditUnion.Batch.KeyStone.Reporting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CreditUnion.Batch.KeyStone.Map;

/// <summary>
/// The reporting-store calls of PL_MapAndAggregate, made with the cu_batch_writer login. Every call opens its own
/// connection (nothing carries between activity attempts) and sets no command timeout of its own: the activity's ADF
/// timeout cancels it. SqlClient connection retries are off (<see cref="Connections"/>, ADR 0009). The data flow's
/// rule is not here: this class only reads its two sources and writes its sink (ADR 0011).
/// </summary>
public sealed class SqlMapAndAggregateStore(IOptions<ReportingSqlOptions> options) : IMapAndAggregateStore
{
    /// <summary>Rows per INSERT statement (6 parameters each, under SQL Server's 2,100-parameter limit).</summary>
    internal const int InsertChunk = 300;

    public async Task LogBatchRunEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await OpenAsync(cancellationToken);
        await BatchRunLog.AppendAsync(connection, request, cancellationToken);
    }

    /// <summary>
    /// <c>ref.usp_UpsertTxnCodeGlMap</c>. Its <c>KEYBRIDGE_MAPPING_INVALID</c> errors (53000 to 53004) become
    /// <see cref="GlMappingInvalidException"/>, which is not retried; the procedure's message names no value.
    /// </summary>
    public async Task UpsertGlMappingAsync(CacheGlMappingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Procedure(connection, "ref.usp_UpsertTxnCodeGlMap");
        command.Parameters.Add(new SqlParameter("@MappingJson", SqlDbType.NVarChar, -1) { Value = request.MappingsJson });
        command.Parameters.Add(new SqlParameter("@SourceVersion", SqlDbType.VarChar, -1) { Value = (object?)request.SourceVersion ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@BatchRunId", SqlDbType.UniqueIdentifier) { Value = request.BatchRunId });
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException e) when (e.Number is >= 53000 and <= 53004)
        {
            throw new GlMappingInvalidException(e.Message);
        }
    }

    public async Task<UnmappedTxnCodes> GetUnmappedTxnCodesAsync(DateOnly businessDate, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Procedure(connection, "ref.usp_GetUnmappedTxnCodes");
        command.Parameters.Add(Date("@BusinessDate", businessDate));
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("ref.usp_GetUnmappedTxnCodes returned no row; it always returns exactly one.");
        }

        return new UnmappedTxnCodes(reader.GetInt32(reader.GetOrdinal("UnmappedCount")), reader.GetString(reader.GetOrdinal("UnmappedCodes")));
    }

    public async Task<IReadOnlyList<GlMapping>> ReadGlMapAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT TxnCode, GlAccountNbr, DrCr, AllocationRatio FROM ref.TxnCodeGlMap", connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<GlMapping>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new GlMapping(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDecimal(3)));
        }

        return rows;
    }

    /// <summary>The data flow's source query, with a parameter instead of a concatenated date. Streamed: one row in memory at a time.</summary>
    public async IAsyncEnumerable<PostedAmount> ReadPostedAmountsAsync(DateOnly businessDate, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT TxnId, TxnCode, PostingDate, Amount FROM core.PostedTransaction WHERE PostingDate = @BusinessDate", connection) { CommandTimeout = 0 };
        command.Parameters.Add(Date("@BusinessDate", businessDate));
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return new PostedAmount(reader.GetInt64(0), reader.GetString(1), DateOnly.FromDateTime(reader.GetDateTime(2)), reader.GetDecimal(3));
        }
    }

    /// <summary>
    /// The sink with its pre-SQL: <c>DELETE … WHERE BusinessDate</c> and the inserts commit together, so a failed attempt
    /// leaves the previous totals, a retry or rerun replaces them, and no reader sees a half-written date.
    /// </summary>
    public async Task ReplaceDailyGlTotalsAsync(DateOnly businessDate, IReadOnlyList<DailyGlTotal> totals, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(totals);
        if (totals.Any(t => t.BusinessDate != businessDate))
        {
            throw new ArgumentException("Every total must belong to the business date being replaced.", nameof(totals));
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var delete = new SqlCommand("DELETE FROM core.DailyGlTotals WHERE BusinessDate = @BusinessDate", connection, transaction) { CommandTimeout = 0 })
        {
            delete.Parameters.Add(Date("@BusinessDate", businessDate));
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var chunk in totals.Chunk(InsertChunk))
        {
            await using var insert = Insert(connection, transaction, chunk);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static SqlCommand Insert(SqlConnection connection, SqlTransaction transaction, DailyGlTotal[] rows)
    {
        var sql = new StringBuilder("INSERT INTO core.DailyGlTotals (BusinessDate, GlAccountNbr, DebitTotal, CreditTotal, NetAmount, TxnCount) VALUES ");
        var command = new SqlCommand { Connection = connection, Transaction = transaction, CommandTimeout = 0 };
        for (var i = 0; i < rows.Length; i++)
        {
            var p = i.ToString(CultureInfo.InvariantCulture);
            sql.Append(i == 0 ? string.Empty : ", ").Append(CultureInfo.InvariantCulture, $"(@d{p}, @a{p}, @dr{p}, @cr{p}, @n{p}, @c{p})");
            command.Parameters.Add(Date("@d" + p, rows[i].BusinessDate));
            command.Parameters.Add(new SqlParameter("@a" + p, SqlDbType.Char, 10) { Value = rows[i].GlAccountNbr });
            command.Parameters.Add(Money("@dr" + p, rows[i].DebitTotal));
            command.Parameters.Add(Money("@cr" + p, rows[i].CreditTotal));
            command.Parameters.Add(Money("@n" + p, rows[i].NetAmount));
            command.Parameters.Add(new SqlParameter("@c" + p, SqlDbType.Int) { Value = rows[i].TxnCount });
        }

        command.CommandText = sql.ToString();
        return command;
    }

    /// <summary>decimal(19,2). The aggregator's values have at most two decimals, so the parameter never rounds.</summary>
    private static SqlParameter Money(string name, decimal value)
    {
        if (decimal.Round(value, 2) != value)
        {
            throw new ArgumentException($"{name} has more than two decimals; DailyGlTotals amounts are sums of cent-rounded allocations.", nameof(value));
        }

        return new SqlParameter(name, SqlDbType.Decimal) { Precision = 19, Scale = 2, Value = value };
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
}
