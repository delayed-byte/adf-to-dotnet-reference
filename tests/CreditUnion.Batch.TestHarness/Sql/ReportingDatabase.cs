using System.Data;
using System.Data.SqlTypes;
using System.Globalization;
using CreditUnion.Batch.DbDeploy;
using CreditUnion.Batch.TestData.Canonical;
using CreditUnion.Batch.TestData.Golden;
using Microsoft.Data.SqlClient;

namespace CreditUnion.Batch.TestHarness.Sql;

/// <summary>
/// The reporting store in the SQL Server container: deployment with the DbUp deployer (ADR 0002),
/// reset between scenarios, and canonical snapshots of the golden tables (ADR 0006).
/// </summary>
public sealed class ReportingDatabase(string adminConnectionString, string writerConnectionString, string readerConnectionString)
{
    /// <summary>Run data cleared between scenarios. Configuration (ExtractConfig, ReconSignRule, RetentionPolicy) is kept.</summary>
    public static IReadOnlyList<string> RunDataTables { get; } =
    [
        "stg.KsAccount", "stg.KsPostedTxn", "stg.KsGlEntry", "stg.CardSettlementRaw", "stg.CardSettlementValidationError", "stg.CardSettlement",
        "core.Account", "core.PostedTransaction", "core.GlEntry", "core.CardSettlement", "core.DailyGlTotals", "ref.TxnCodeGlMap",
        "ops.BatchRun", "ops.CardFileLog", "ops.ReconExceptions",
    ];

    /// <summary>sa on the Reporting database: deploys and resets. Tests of the batch use the writer.</summary>
    public string AdminConnectionString { get; } = adminConnectionString;

    /// <summary>cu_batch_writer, the login the batch uses.</summary>
    public string WriterConnectionString { get; } = writerConnectionString;

    /// <summary>cu_report_reader, read-only consumers (AT-19).</summary>
    public string ReaderConnectionString { get; } = readerConnectionString;

    public void Deploy(TestSecrets secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var result = ReportingDatabaseDeployer.Deploy(AdminConnectionString, RepositoryPaths.ReportingScripts,
            new ReportingLogins(secrets.SqlWriterPassword, secrets.SqlReaderPassword));
        if (!result.Successful)
        {
            throw new InvalidOperationException("Deploying the reporting store failed.", result.Error);
        }
    }

    /// <summary>
    /// Empties the run-data tables. TRUNCATE (as sa) is used because it does not fire the append-only
    /// triggers on ops.BatchRun, ops.CardFileLog and ops.ReconExceptions; the batch's own logins cannot do this.
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(AdminConnectionString, cancellationToken).ConfigureAwait(false);
        foreach (var table in RunDataTables)
        {
            await using var command = new SqlCommand($"TRUNCATE TABLE {table}", connection);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Snapshot of a golden table. <paramref name="runIds"/> are the scenario's batch run IDs in run order
    /// (run 1 first); they replace the <c>{runs}</c> token so run-specific GUIDs map to run ordinals.
    /// </summary>
    public async Task<TableData> SnapshotAsync(GoldenTableDefinition table, IReadOnlyList<Guid> runIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(runIds);
        await using var connection = await OpenAsync(AdminConnectionString, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(RunsSql(table.SnapshotSql, runIds), connection);
        for (var i = 0; i < runIds.Count; i++)
        {
            command.Parameters.Add(new SqlParameter("@run" + (i + 1).ToString(CultureInfo.InvariantCulture), SqlDbType.UniqueIdentifier) { Value = runIds[i] });
        }

        return await ReadAsync(command, table.Name, table.Columns, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a query and returns canonical rows typed by <paramref name="columns"/>; the SQL types must match.</summary>
    public static async Task<TableData> ReadAsync(SqlCommand command, string name, IReadOnlyList<ColumnDefinition> columns, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(columns);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (reader.FieldCount != columns.Count)
        {
            throw new InvalidOperationException($"{name}: query returns {reader.FieldCount} columns; the definition has {columns.Count}.");
        }

        for (var i = 0; i < columns.Count; i++)
        {
            var actual = reader.GetDataTypeName(i);
            if (!string.Equals(reader.GetName(i), columns[i].Name, StringComparison.Ordinal) || !SqlTypeMatches(actual, columns[i].Type.Kind))
            {
                throw new InvalidOperationException($"{name}: column {i + 1} is {reader.GetName(i)} {actual}; the definition says {columns[i].Name} {columns[i].Type}.");
            }
        }

        var rows = new List<string?[]>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add([.. columns.Select((c, i) => Canonical(reader, i, c.Type))]);
        }

        return new TableData(name, [.. columns.Select(c => c.Name)], rows);
    }

    /// <summary>Bulk-inserts canonical rows into a table (self-tests and cross-checks only; the batch never does this).</summary>
    public async Task BulkInsertAsync(string table, IReadOnlyList<ColumnDefinition> columns, TableData rows, IReadOnlyDictionary<string, object>? constants = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        using var data = new DataTable();
        foreach (var c in columns)
        {
            data.Columns.Add(c.Name, ClrType(c.Type));
        }

        foreach (var (name, value) in constants ?? new Dictionary<string, object>())
        {
            data.Columns.Add(name, value.GetType()).DefaultValue = value;
        }

        foreach (var row in rows.Rows)
        {
            var values = new object[data.Columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                values[i] = Clr(row[i], columns[i].Type);
            }

            for (var i = columns.Count; i < values.Length; i++)
            {
                values[i] = data.Columns[i].DefaultValue;
            }

            data.Rows.Add(values);
        }

        await using var connection = await OpenAsync(AdminConnectionString, cancellationToken).ConfigureAwait(false);
        using var bulk = new SqlBulkCopy(connection) { DestinationTableName = table };
        foreach (DataColumn c in data.Columns)
        {
            bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
        }

        await bulk.WriteToServerAsync(data, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    public static string RunsSql(string sql, IReadOnlyList<Guid> runIds)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(runIds);
        var values = runIds.Count == 0
            ? "(VALUES (CAST(NULL AS uniqueidentifier), 0))"
            : "(VALUES " + string.Join(", ", runIds.Select((_, i) => string.Create(CultureInfo.InvariantCulture, $"(@run{i + 1}, {i + 1})"))) + ")";
        return sql.Replace("{runs}", values, StringComparison.Ordinal);
    }

    private static string? Canonical(SqlDataReader reader, int i, ColumnType type)
    {
        if (reader.IsDBNull(i))
        {
            return null;
        }

        return type.Kind switch
        {
            SqlKind.Char or SqlKind.VarChar or SqlKind.NVarChar => reader.GetString(i),
            SqlKind.Decimal => CanonicalValue.Decimal(reader.GetSqlDecimal(i), type.Scale),
            SqlKind.Date => CanonicalValue.Date(DateOnly.FromDateTime(reader.GetDateTime(i))),
            SqlKind.DateTime2 => CanonicalValue.Timestamp(reader.GetDateTime(i), type.Precision),
            SqlKind.BigInt => CanonicalValue.Integer(reader.GetInt64(i)),
            SqlKind.Int => CanonicalValue.Integer(reader.GetInt32(i)),
            SqlKind.Bit => CanonicalValue.Bit(reader.GetBoolean(i)),
            _ => throw new NotSupportedException(type.ToString()),
        };
    }

    private static bool SqlTypeMatches(string sqlType, SqlKind kind) => kind switch
    {
        SqlKind.Char => sqlType is "char" or "nchar",
        SqlKind.VarChar => sqlType is "varchar",
        SqlKind.NVarChar => sqlType is "nvarchar",
        SqlKind.Decimal => sqlType is "decimal" or "numeric",
        SqlKind.Date => sqlType is "date",
        SqlKind.DateTime2 => sqlType is "datetime2",
        SqlKind.BigInt => sqlType is "bigint",
        SqlKind.Int => sqlType is "int",
        SqlKind.Bit => sqlType is "bit",
        _ => false,
    };

    private static Type ClrType(ColumnType type) => type.Kind switch
    {
        SqlKind.Decimal => typeof(SqlDecimal),
        SqlKind.Date or SqlKind.DateTime2 => typeof(DateTime),
        SqlKind.BigInt => typeof(long),
        SqlKind.Int => typeof(int),
        SqlKind.Bit => typeof(bool),
        _ => typeof(string),
    };

    private static object Clr(string? text, ColumnType type) => text is null ? DBNull.Value : type.Kind switch
    {
        SqlKind.Decimal => SqlDecimal.Parse(text),
        SqlKind.Date => CanonicalValue.ParseDate(text).ToDateTime(TimeOnly.MinValue),
        SqlKind.DateTime2 => CanonicalValue.ParseTimestamp(text),
        SqlKind.BigInt => long.Parse(text, CultureInfo.InvariantCulture),
        SqlKind.Int => int.Parse(text, CultureInfo.InvariantCulture),
        SqlKind.Bit => text == "true",
        _ => text,
    };
}
