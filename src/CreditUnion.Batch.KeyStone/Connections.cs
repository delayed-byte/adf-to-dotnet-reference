using CreditUnion.Batch.Core.Configuration;
using IBM.Data.Db2;
using Microsoft.Data.SqlClient;

namespace CreditUnion.Batch.KeyStone;

/// <summary>
/// Opens the two connections of the extract. Retrying is the orchestrator's job (a Durable
/// <c>RetryPolicy</c> built from the ADF activity policy, ADR 0008, ADR 0009), so the drivers must not
/// retry underneath it: that would multiply the attempts and hide them from the audit trail.
/// </summary>
public static class Connections
{
    /// <summary>
    /// The reporting store (login cu_batch_writer). <c>ConnectRetryCount = 0</c> switches off the SqlClient
    /// connection-resiliency retries, which are on by default (one retry of a failed or broken connection).
    /// </summary>
    public static SqlConnection CreateReportingConnection(ReportingSqlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = new SqlConnectionStringBuilder(options.ConnectionString) { ConnectRetryCount = 0 };
        return new SqlConnection(builder.ConnectionString);
    }

    /// <summary>
    /// KeyStone Db2 (login KSEXTRACT). Net.IBM.Data.Db2 10.0 has no connection-string keyword that retries
    /// a connection (it exposes only <c>Connect Timeout</c>), and automatic client reroute applies only
    /// when the server advertises an alternate server, which KeyStone does not (ADR 0009 records the check
    /// AT-04 makes).
    /// </summary>
    public static DB2Connection CreateKeyStoneConnection(KeyStoneDb2Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new DB2Connection(options.ConnectionString);
    }
}
