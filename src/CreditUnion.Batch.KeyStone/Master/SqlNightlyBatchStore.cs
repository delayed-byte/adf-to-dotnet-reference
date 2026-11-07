using System.Data;
using System.Runtime.CompilerServices;
using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Master;
using CreditUnion.Batch.KeyStone.Reporting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CreditUnion.Batch.KeyStone.Master;

/// <summary>
/// The reporting-store calls of PL_Master_NightlyBatch, made with the cu_batch_writer login. Every call opens its own connection
/// and sets no command timeout of its own: the activity's ADF timeout (equal to the Copy source's <c>queryTimeout</c>) cancels it.
/// SqlClient connection retries are off (<see cref="Connections"/>, ADR 0009). The procedures hold the rules (ADR 0015).
/// </summary>
public sealed class SqlNightlyBatchStore(IOptions<ReportingSqlOptions> options) : INightlyBatchStore
{
    public async Task LogBatchRunEventAsync(LogBatchRunEventRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await OpenAsync(cancellationToken);
        await BatchRunLog.AppendAsync(connection, request, cancellationToken);
    }

    public async IAsyncEnumerable<RunEvidenceEntry> ReadRunEvidenceAsync(Guid batchRunId, DateOnly businessDate, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand("ops.usp_GetRunEvidence", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 0 };
        command.Parameters.Add(new SqlParameter("@BatchRunId", SqlDbType.UniqueIdentifier) { Value = batchRunId });
        command.Parameters.Add(new SqlParameter("@BusinessDate", SqlDbType.Date) { Value = businessDate.ToDateTime(TimeOnly.MinValue) });
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        var section = reader.GetOrdinal("section");
        var item = reader.GetOrdinal("item");
        var value = reader.GetOrdinal("value");
        while (await reader.ReadAsync(cancellationToken))
        {
            // SequentialAccess: read the columns in order, each once.
            var sectionValue = reader.GetString(section);
            var itemValue = reader.GetString(item);
            var valueValue = await reader.IsDBNullAsync(value, cancellationToken) ? null : reader.GetString(value);
            yield return new RunEvidenceEntry(sectionValue, itemValue, valueValue);
        }
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
}
