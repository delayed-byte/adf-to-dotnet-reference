using CreditUnion.Batch.DbDeploy;
using CreditUnion.Batch.TestData.Canonical;
using CreditUnion.Batch.TestData.KeyStone;
using CreditUnion.Batch.TestHarness.Scenarios;
using IBM.Data.Db2;

namespace CreditUnion.Batch.TestHarness.Db2;

/// <summary>
/// The synthetic KeyStone database in the Db2 container. Deploys the schema with the same DbUp
/// deployer as <c>./build.ps1 seed</c> (ADR 0002), resets scenario data between scenarios (reference
/// data from migration 0006 is kept) and loads a scenario's input tables.
/// </summary>
public sealed class KeyStoneDb2(string adminConnectionString, string extractConnectionString, string host, int port)
{
    private const int LiteralInsertThreshold = 5_000;
    private const int LiteralBatchRows = 500;
    private const int DeleteChunkRows = 50_000;
    private HashSet<string>? baseTxnCodes;

    /// <summary>Instance owner (db2inst1): deploys, resets and loads.</summary>
    public string AdminConnectionString { get; } = adminConnectionString;

    /// <summary>Read-only extract login KSEXTRACT (ADR 0003), as the batch connects in production.</summary>
    public string ExtractConnectionString { get; } = extractConnectionString;

    public string Host { get; } = host;

    public int Port { get; } = port;

    /// <summary>The extract connection string pointed at another endpoint, e.g. a <see cref="Faults.FaultInjectingTcpProxy"/>.</summary>
    public string ExtractConnectionStringVia(string host, int port)
    {
        var builder = new DB2ConnectionStringBuilder(ExtractConnectionString);
        builder["Server"] = $"{host}:{port}";
        return builder.ConnectionString;
    }

    public void Deploy()
    {
        var result = KeyStoneDb2Deployer.Deploy(AdminConnectionString, RepositoryPaths.Db2Scripts);
        if (!result.Successful)
        {
            throw new InvalidOperationException("Deploying the KeyStone Db2 schema failed.", result.Error);
        }
    }

    /// <summary>Deletes all scenario rows, children first; keeps the migration's GL accounts and transaction codes.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        baseTxnCodes ??= [.. (await ReadAsync(connection, KeyStoneTables.TxnCode, cancellationToken).ConfigureAwait(false)).Rows.Select(r => r[0]!)];
        foreach (var table in KeyStoneTables.LoadOrder.Reverse().Where(t => t != KeyStoneTables.TxnCode))
        {
            await DeleteAllAsync(connection, table.Name, cancellationToken).ConfigureAwait(false);
        }

        var current = await ReadAsync(connection, KeyStoneTables.TxnCode, cancellationToken).ConfigureAwait(false);
        foreach (var code in current.Rows.Select(r => r[0]!).Where(c => !baseTxnCodes.Contains(c)))
        {
            using var delete = new DB2Command("DELETE FROM KS.TXN_CODE WHERE TXN_CODE = ?", connection);
            var p = delete.CreateParameter();
            Db2Values.Bind(p, code, KeyStoneTables.TxnCode.Columns[0].Type);
            delete.Parameters.Add(p);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Inserts the scenario's rows, parents first, in one transaction per table.</summary>
    public async Task LoadAsync(Scenario scenario, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        await using var connection = await OpenAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var table in KeyStoneTables.LoadOrder)
        {
            await InsertAsync(connection, table, scenario.InputTable(table), cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task InsertAsync(DB2Connection connection, Db2TableDefinition table, TableData rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.RowCount == 0)
        {
            return;
        }

        if (!rows.Columns.SequenceEqual(table.Columns.Select(c => c.Name), StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"{table.Name} input columns do not match the table definition.");
        }

        if (rows.RowCount >= LiteralInsertThreshold)
        {
            await InsertLiteralBatchesAsync(connection, table, rows, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var transaction = connection.BeginTransaction();
        using var command = new DB2Command(
            $"INSERT INTO {table.Name} ({string.Join(", ", rows.Columns)}) VALUES ({string.Join(", ", rows.Columns.Select(_ => "?"))})", connection, transaction);
        var parameters = table.Columns.Select(_ => command.Parameters.Add(command.CreateParameter())).ToArray();
        foreach (var row in rows.Rows)
        {
            for (var i = 0; i < parameters.Length; i++)
            {
                Db2Values.Bind(parameters[i], row[i], table.Columns[i].Type);
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    /// <summary>
    /// Large tables (the performance scenario has a million transactions) go in as multi-row INSERT
    /// statements with exact literals, <see cref="LiteralBatchRows"/> rows each. Row-by-row parameter binding takes
    /// minutes for a million rows; the literals are as exact as the bound parameters (Db2Values.ToLiteral).
    /// Each statement commits by itself: one transaction of a million rows overflows the test database's log.
    /// </summary>
    private static async Task InsertLiteralBatchesAsync(DB2Connection connection, Db2TableDefinition table, TableData rows, CancellationToken cancellationToken)
    {
        var header = $"INSERT INTO {table.Name} ({string.Join(", ", rows.Columns)}) VALUES ";
        var text = new System.Text.StringBuilder();
        for (var start = 0; start < rows.RowCount; start += LiteralBatchRows)
        {
            text.Clear().Append(header);
            var end = Math.Min(start + LiteralBatchRows, rows.RowCount);
            for (var r = start; r < end; r++)
            {
                text.Append(r == start ? "(" : ", (");
                for (var c = 0; c < table.Columns.Count; c++)
                {
                    text.Append(c == 0 ? string.Empty : ", ").Append(Db2Values.ToLiteral(rows.Rows[r][c], table.Columns[c].Type));
                }

                text.Append(')');
            }

            using var command = new DB2Command(text.ToString(), connection) { CommandTimeout = 0 };
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads a whole KeyStone table into canonical form by the ADR 0005 rules, ordered by key.</summary>
    public async Task<TableData> ReadAsync(Db2TableDefinition table, string? where = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return await ReadAsync(connection, table, cancellationToken, where).ConfigureAwait(false);
    }

    public async Task<DB2Connection> OpenAsync(string? connectionString = null, CancellationToken cancellationToken = default)
    {
        var connection = new DB2Connection(connectionString ?? AdminConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<TableData> ReadAsync(DB2Connection connection, Db2TableDefinition table, CancellationToken cancellationToken, string? where = null)
    {
        using var command = new DB2Command(
            $"SELECT {string.Join(", ", table.Columns.Select(c => c.Name))} FROM {table.Name}{(where is null ? string.Empty : " WHERE " + where)} ORDER BY {string.Join(", ", table.KeyColumns)}",
            connection);
        using var reader = (DB2DataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<string?[]>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add([.. table.Columns.Select((c, i) => Db2Values.ReadCanonical(reader, i, c.Type))]);
        }

        return new TableData(table.Name, [.. table.Columns.Select(c => c.Name)], rows);
    }

    /// <summary>Deletes every row in chunks that commit separately, so clearing the performance scenario's million rows fits the log.</summary>
    private static async Task DeleteAllAsync(DB2Connection connection, string tableName, CancellationToken cancellationToken)
    {
        int deleted;
        do
        {
            using var command = new DB2Command($"DELETE FROM (SELECT 1 FROM {tableName} FETCH FIRST {DeleteChunkRows} ROWS ONLY)", connection) { CommandTimeout = 0 };
            deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        while (deleted == DeleteChunkRows);
    }

    private static async Task ExecuteAsync(DB2Connection connection, string sql, CancellationToken cancellationToken)
    {
        using var command = new DB2Command(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
