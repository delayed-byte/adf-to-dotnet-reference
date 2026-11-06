using System.Data;
using CreditUnion.Batch.Core.Extract;
using Microsoft.Data.SqlClient;

namespace CreditUnion.Batch.KeyStone.Reporting;

/// <summary>One call of <c>ops.usp_LogBatchRunEvent</c>, shared by every pipeline's Log activities.</summary>
internal static class BatchRunLog
{
    public static async Task AppendAsync(SqlConnection connection, LogBatchRunEventRequest request, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("ops.usp_LogBatchRunEvent", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 0 };
        command.Parameters.Add(new SqlParameter("@BatchRunId", SqlDbType.UniqueIdentifier) { Value = request.BatchRunId });
        command.Parameters.Add(new SqlParameter("@BusinessDate", SqlDbType.Date) { Value = request.BusinessDate.ToDateTime(TimeOnly.MinValue) });
        command.Parameters.Add(new SqlParameter("@PipelineName", SqlDbType.VarChar, 100) { Value = request.PipelineName });
        command.Parameters.Add(new SqlParameter("@Status", SqlDbType.VarChar, 30) { Value = request.Status });
        command.Parameters.Add(new SqlParameter("@StepName", SqlDbType.VarChar, 100) { Value = OrNull(request.StepName) });
        command.Parameters.Add(new SqlParameter("@ObjectName", SqlDbType.NVarChar, 400) { Value = OrNull(request.ObjectName) });
        command.Parameters.Add(new SqlParameter("@RecordCount", SqlDbType.BigInt) { Value = OrNull(request.RecordCount) });
        command.Parameters.Add(new SqlParameter("@ErrorCode", SqlDbType.VarChar, 50) { Value = OrNull(request.ErrorCode) });
        command.Parameters.Add(new SqlParameter("@ErrorMessage", SqlDbType.NVarChar, 2000) { Value = OrNull(request.ErrorMessage) });
        command.Parameters.Add(new SqlParameter("@Notes", SqlDbType.NVarChar, 2000) { Value = OrNull(request.Notes) });
        command.Parameters.Add(new SqlParameter("@StartedUtc", SqlDbType.DateTime2) { Scale = 3, Value = OrNull(request.StartedUtc) });
        command.Parameters.Add(new SqlParameter("@EndedUtc", SqlDbType.DateTime2) { Scale = 3, Value = OrNull(request.EndedUtc) });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object OrNull<T>(T? value) => value is null ? DBNull.Value : value;
}
