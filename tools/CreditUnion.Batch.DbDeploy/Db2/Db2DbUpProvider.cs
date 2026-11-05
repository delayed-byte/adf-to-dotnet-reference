using System.Data;
using System.Globalization;
using DbUp.Engine;
using DbUp.Engine.Output;
using DbUp.Engine.Transactions;
using DbUp.Support;
using IBM.Data.Db2;

namespace CreditUnion.Batch.DbDeploy.Db2;

// DbUp has no IBM Db2 provider, so this file supplies the three pieces DbUp needs:
// a connection manager, a script executor and a journal table. See ADR 0002.

internal sealed class Db2ConnectionManager(string connectionString)
    : DatabaseConnectionManager(_ => new DB2Connection(connectionString))
{
    public override IEnumerable<string> SplitScriptIntoCommands(string scriptContents) =>
        Db2ScriptSplitter.Split(scriptContents);
}

internal sealed class Db2ObjectParser() : SqlObjectParser("\"", "\"");

internal sealed class Db2ScriptExecutor(
    Func<IConnectionManager> connectionManagerFactory,
    Func<IUpgradeLog> log,
    Func<bool> variablesEnabled,
    IEnumerable<IScriptPreprocessor> scriptPreprocessors,
    Func<IJournal> journalFactory)
    : ScriptExecutor(connectionManagerFactory, new Db2ObjectParser(), log, null, variablesEnabled, scriptPreprocessors, journalFactory)
{
    private readonly Func<IUpgradeLog> log = log;

    protected override string GetVerifySchemaSql(string schema) =>
        throw new NotSupportedException("Schemas are created explicitly by the Db2 migration scripts.");

    protected override void ExecuteCommandsWithinExceptionHandler(int index, SqlScript script, Action executeCallback)
    {
        try
        {
            executeCallback();
        }
        catch (DB2Exception ex)
        {
            log().LogError(
                "Db2 script {0} failed at statement {1}: {2}",
                script.Name,
                index.ToString(CultureInfo.InvariantCulture),
                ex.Message);
            throw;
        }
    }
}

internal sealed class Db2TableJournal(
    Func<IConnectionManager> connectionManager,
    Func<IUpgradeLog> logger,
    string schema,
    string table)
    : TableJournal(connectionManager, logger, new Db2ObjectParser(), schema, table)
{
    private readonly string schemaName = schema;
    private readonly string tableName = table;

    protected override string GetInsertJournalEntrySql(string scriptName, string applied) =>
        throw new NotSupportedException("Journal inserts use positional parameters; see GetInsertScriptCommand.");

    // The IBM provider binds positional '?' markers, so build the insert command directly.
    protected override IDbCommand GetInsertScriptCommand(Func<IDbCommand> dbCommandFactory, SqlScript script)
    {
        ArgumentNullException.ThrowIfNull(dbCommandFactory);
        ArgumentNullException.ThrowIfNull(script);

        var command = dbCommandFactory();
        command.CommandText = $"INSERT INTO {FqSchemaTableName} (SCRIPT_NAME, APPLIED) VALUES (?, CURRENT TIMESTAMP)";
        var parameter = command.CreateParameter();
        parameter.Value = script.Name;
        command.Parameters.Add(parameter);
        return command;
    }

    protected override string GetJournalEntriesSql() =>
        $"SELECT SCRIPT_NAME FROM {FqSchemaTableName} ORDER BY SCRIPT_NAME";

    protected override string CreateSchemaTableSql(string quotedPrimaryKeyName) =>
        $"CREATE TABLE {FqSchemaTableName} (" +
        "SCHEMA_VERSION_ID INTEGER NOT NULL GENERATED ALWAYS AS IDENTITY, " +
        "SCRIPT_NAME VARCHAR(255) NOT NULL, " +
        "APPLIED TIMESTAMP NOT NULL, " +
        $"CONSTRAINT {quotedPrimaryKeyName} PRIMARY KEY (SCHEMA_VERSION_ID))";

    protected override bool DoesTableExist(Func<IDbCommand> dbCommandFactory)
    {
        ArgumentNullException.ThrowIfNull(dbCommandFactory);

        using var command = dbCommandFactory();
        command.CommandText = "SELECT COUNT(*) FROM SYSCAT.TABLES WHERE TABSCHEMA = ? AND TABNAME = ?";
        AddParameter(command, schemaName);
        AddParameter(command, tableName);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static void AddParameter(IDbCommand command, string value)
    {
        var parameter = command.CreateParameter();
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
