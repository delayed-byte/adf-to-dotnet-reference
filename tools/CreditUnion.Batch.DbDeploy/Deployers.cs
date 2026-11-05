using CreditUnion.Batch.DbDeploy.Db2;
using DbUp;
using DbUp.Builder;
using DbUp.Engine;
using DbUp.Support;
using Microsoft.Data.SqlClient;

namespace CreditUnion.Batch.DbDeploy;

/// <summary>Credentials for the least-privilege reporting logins created by the deployer.</summary>
public sealed record ReportingLogins(string WriterPassword, string ReaderPassword);

/// <summary>
/// Deploys the SQL Server reporting store from <c>db/reporting</c>:
/// <c>migrations</c> run once (journaled), then <c>procedures</c> and <c>security</c> run on every
/// deployment (they are idempotent: CREATE OR ALTER, IF NOT EXISTS).
/// </summary>
public static class ReportingDatabaseDeployer
{
    public static DatabaseUpgradeResult Deploy(string connectionString, string scriptsRoot, ReportingLogins logins)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptsRoot);
        ArgumentNullException.ThrowIfNull(logins);

        var databaseName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new ArgumentException("The connection string must name the reporting database (Initial Catalog).", nameof(connectionString));
        }

        EnsureDatabase.For.SqlDatabase(connectionString);

        return DeployChanges.To
            .SqlDatabase(connectionString)
            .JournalToSqlTable("dbo", "SchemaVersions")
            .WithScriptsFromFileSystem(Path.Combine(scriptsRoot, "migrations"), new SqlScriptOptions { ScriptType = ScriptType.RunOnce, RunGroupOrder = 1 })
            .WithScriptsFromFileSystem(Path.Combine(scriptsRoot, "procedures"), new SqlScriptOptions { ScriptType = ScriptType.RunAlways, RunGroupOrder = 2 })
            .WithScriptsFromFileSystem(Path.Combine(scriptsRoot, "security"), new SqlScriptOptions { ScriptType = ScriptType.RunAlways, RunGroupOrder = 3 })
            .WithVariable("WriterPassword", logins.WriterPassword)
            .WithVariable("ReaderPassword", logins.ReaderPassword)
            .WithVariable("DatabaseName", databaseName)
            .WithTransactionPerScript()
            .LogToConsole()
            .Build()
            .PerformUpgrade();
    }
}

/// <summary>
/// Deploys the synthetic KeyStone schema to Db2 from <c>db/keystone-db2/migrations</c> using the
/// custom DbUp Db2 provider (journal table <c>DBUP.SCHEMA_VERSIONS</c>). The database itself is
/// created by the Db2 container.
/// </summary>
public static class KeyStoneDb2Deployer
{
    public static DatabaseUpgradeResult Deploy(string connectionString, string scriptsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptsRoot);

        var builder = new UpgradeEngineBuilder();
        builder.Configure(c =>
        {
            c.ConnectionManager = new Db2ConnectionManager(connectionString);
            c.Journal = new Db2TableJournal(() => c.ConnectionManager, () => c.Log, "DBUP", "SCHEMA_VERSIONS");
            c.ScriptExecutor = new Db2ScriptExecutor(() => c.ConnectionManager, () => c.Log, () => c.VariablesEnabled, c.ScriptPreprocessors, () => c.Journal);
        });

        return builder
            .WithScriptsFromFileSystem(Path.Combine(scriptsRoot, "migrations"), new SqlScriptOptions { ScriptType = ScriptType.RunOnce, RunGroupOrder = 1 })
            .WithVariablesDisabled()
            .WithTransactionPerScript()
            .LogToConsole()
            .Build()
            .PerformUpgrade();
    }
}
