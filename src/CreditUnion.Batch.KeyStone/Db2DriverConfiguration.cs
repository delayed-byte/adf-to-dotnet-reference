using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace CreditUnion.Batch.KeyStone;

/// <summary>A Db2 endpoint as the CLI driver keys its per-database settings: database name, host and port.</summary>
public sealed record Db2Endpoint(string Database, string Host, int Port);

/// <summary>
/// Switches off the Db2 CLI driver's own connection retry (ADR 0009).
/// <para>
/// The driver retries a failed connect by itself: its automatic client reroute (ACR) makes up to three
/// attempts, the last after a 5-second wait, even when the server advertises no alternate server. That
/// hides failed attempts from the orchestrator's <c>RetryPolicy</c> (ADR 0008): the Copy activity would
/// "succeed" after the driver rode out the outage, with no failed attempt in the audit trail and 3 x 4
/// connection attempts when it does fail. Net.IBM.Data.Db2 10.0 has no connection-string keyword for it, and
/// <c>db2cli.ini</c> does not honour the ACR settings. The only switch is a <c>db2dsdriver.cfg</c> entry
/// that names the exact database, host and port:
/// <code>&lt;database name="KEYSTONE" host="db2.example" port="50000"&gt;&lt;acr&gt;&lt;parameter name="enableACR" value="false"/&gt;...</code>
/// The driver reads that file once, when the process makes its first connection, from the path in
/// <c>DB2DSDRIVER_CFG_PATH</c>; a later change has no effect on the running process.
/// </para>
/// </summary>
public static class Db2DriverConfiguration
{
    public const string ConfigPathVariable = "DB2DSDRIVER_CFG_PATH";

    private const int DefaultPort = 50000;

    /// <summary>
    /// Writes a <c>db2dsdriver.cfg</c> that disables ACR for <paramref name="endpoints"/> and points
    /// <see cref="ConfigPathVariable"/> at it, unless the variable is already set (an operator-supplied file
    /// is then used as it is and must carry the same switch). Must run before the process's first Db2
    /// connection. Returns the path the driver will read, or null when there is nothing to configure.
    /// </summary>
    public static string? DisableClientReroute(IEnumerable<Db2Endpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var existing = Environment.GetEnvironmentVariable(ConfigPathVariable);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var list = endpoints.Distinct().ToList();
        if (list.Count == 0)
        {
            return null;
        }

        var xml = BuildConfig(list);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml)))[..16];
        var directory = Path.Combine(Path.GetTempPath(), "cu-batch-db2", hash);
        var path = Path.Combine(directory, "db2dsdriver.cfg");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }

        Environment.SetEnvironmentVariable(ConfigPathVariable, path);
        return path;
    }

    /// <summary>The same for the endpoint in a Db2 connection string. An empty string configures nothing.</summary>
    public static string? DisableClientReroute(string? connectionString) =>
        string.IsNullOrWhiteSpace(connectionString) ? null : DisableClientReroute([EndpointOf(connectionString)]);

    /// <summary>The database, host and port of a connection string (<c>Server=host:port;Database=name</c>; port 50000 when absent).</summary>
    public static Db2Endpoint EndpointOf(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        string? server = null;
        string? database = null;
        foreach (var pair in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            var key = pair[..equals].Trim();
            var value = pair[(equals + 1)..].Trim();
            if (key.Equals("Server", StringComparison.OrdinalIgnoreCase))
            {
                server = value;
            }
            else if (key.Equals("Database", StringComparison.OrdinalIgnoreCase) || key.Equals("DBName", StringComparison.OrdinalIgnoreCase))
            {
                database = value;
            }
        }

        if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(database))
        {
            throw new ArgumentException("The KeyStone Db2 connection string needs Server=host:port and Database=name.", nameof(connectionString));
        }

        var separator = server.LastIndexOf(':');
        var host = separator < 0 ? server : server[..separator];
        var port = separator < 0 || !int.TryParse(server[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? DefaultPort : parsed;
        return new Db2Endpoint(database, host, port);
    }

    /// <summary>The <c>db2dsdriver.cfg</c> text: one database entry per endpoint, each with ACR disabled.</summary>
    public static string BuildConfig(IReadOnlyCollection<Db2Endpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var xml = new StringBuilder("<configuration>\n  <databases>\n");
        foreach (var endpoint in endpoints)
        {
            xml.Append(CultureInfo.InvariantCulture,
                $"    <database name=\"{SecurityElement.Escape(endpoint.Database)}\" host=\"{SecurityElement.Escape(endpoint.Host)}\" port=\"{endpoint.Port}\">\n")
                .Append("      <acr>\n        <parameter name=\"enableACR\" value=\"false\"/>\n      </acr>\n")
                .Append("    </database>\n");
        }

        return xml.Append("  </databases>\n</configuration>\n").ToString();
    }
}
