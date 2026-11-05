using System.Data;
using System.Data.SqlTypes;
using CreditUnion.Batch.TestData.Canonical;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Db2;
using CreditUnion.Batch.TestHarness.Sql;
using IBM.Data.Db2;
using IBM.Data.DB2Types;
using Microsoft.Data.SqlClient;

namespace CreditUnion.Batch.AcceptanceTests.Harness;

/// <summary>
/// Pins the driver and database behaviour that ADR 0005 is based on (Net.IBM.Data.Db2 10.0, Db2 12.1,
/// Microsoft.Data.SqlClient 7.1, SQL Server 2022). If an upgrade changes any of it, these tests fail
/// and the ADR must be revisited before the extract can be trusted.
/// </summary>
public sealed class TypeHandlingProbeTests(BatchEnvironmentFixture fixture) : IAsyncLifetime
{
    private const string Max31 = "99999999999999999999999.99999999";
    private const string Mixed31 = "12345678901234567890123.45678901";
    private const string NonLatin1 = "Café Zoë – Ω ✓ 日本";

    private BatchEnvironment Env => fixture.Environment;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var c = await Env.KeyStone.OpenAsync(cancellationToken: Ct);
        await TryAsync(c, "DROP TABLE HARNESS.TYPEPROBE");
        await ExecAsync(c, "CREATE TABLE HARNESS.TYPEPROBE (ID INT NOT NULL, C3 CHAR(3), C10 CHAR(10), V VARCHAR(60), D31 DECIMAL(31,8), TS TIMESTAMP(6))");
        await InsertAsync(c, 1, "BR", "2100", "TRAILING   ", Max31, new DateTime(2026, 3, 8, 2, 30, 0).AddTicks(1_234_560));
        await InsertAsync(c, 2, " LN", "2100      ", string.Empty, Mixed31, new DateTime(2026, 11, 1, 1, 30, 0));
        await InsertAsync(c, 3, null, null, null, null, null);
        await InsertAsync(c, 4, "ATM", "1010", NonLatin1, "-" + Max31, new DateTime(2026, 11, 4, 23, 59, 59).AddTicks(9_999_990));
    }

    public async ValueTask DisposeAsync()
    {
        await using var c = await Env.KeyStone.OpenAsync(cancellationToken: Ct);
        await TryAsync(c, "DROP TABLE HARNESS.TYPEPROBE");
    }

    [Fact]
    public async Task Db2_char_values_come_back_padded_and_varchar_keeps_trailing_spaces_empty_strings_and_nulls()
    {
        var rows = await ReadAsync(r => (r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));

        rows[1].ShouldBe(("BR ", "2100      ", "TRAILING   "));
        rows[2].ShouldBe((" LN", "2100      ", string.Empty));
        rows[3].ShouldBe((null, null, null));
        rows[4].Item3.ShouldBe(NonLatin1);
    }

    [Fact]
    public async Task Db2_GetDecimal_silently_rounds_31_digit_values_so_the_extract_must_read_DB2Decimal()
    {
        var rows = await ReadAsync(r => r.IsDBNull(4) ? ((decimal Rounded, string Exact)?)null : (r.GetDecimal(4), r.GetDB2Decimal(4).ToString()));

        rows[1]!.Value.Rounded.ShouldBe(100_000_000_000_000_000_000_000m, "no exception: the value is silently rounded");
        rows[1]!.Value.Exact.ShouldBe(Max31);
        rows[2]!.Value.Rounded.ShouldBe(12_345_678_901_234_567_890_123.456789m);
        rows[2]!.Value.Exact.ShouldBe(Mixed31);
        rows[4]!.Value.Exact.ShouldBe("-" + Max31);
    }

    [Fact]
    public async Task Db2_string_parameters_lose_decimal_digits_but_DB2Decimal_parameters_do_not()
    {
        await using var c = await Env.KeyStone.OpenAsync(cancellationToken: Ct);
        using (var insert = new DB2Command("INSERT INTO HARNESS.TYPEPROBE (ID, D31) VALUES (9, ?)", c))
        {
            insert.Parameters.Add(new DB2Parameter { DB2Type = DB2Type.Decimal, Value = Mixed31 });
            await insert.ExecuteNonQueryAsync(Ct);
        }

        var rows = await ReadAsync(r => r.IsDBNull(4) ? null : r.GetDB2Decimal(4).ToString());
        rows[9].ShouldBe("12345678901234567890123.45678900", "the driver converted the string through System.Decimal");
        rows[2].ShouldBe(Mixed31, "the DB2Decimal parameter kept every digit");
    }

    [Fact]
    public async Task Db2_timestamps_are_naive_wall_clock_values_with_microseconds_even_inside_dst_gaps()
    {
        var rows = await ReadAsync(r => r.IsDBNull(5) ? (DateTime?)null : r.GetDateTime(5));

        rows[1]!.Value.Kind.ShouldBe(DateTimeKind.Unspecified);
        CanonicalValue.Timestamp(rows[1]!.Value, 6).ShouldBe("2026-03-08T02:30:00.123456", "a local time that does not exist in Pacific time is stored verbatim");
        CanonicalValue.Timestamp(rows[2]!.Value, 6).ShouldBe("2026-11-01T01:30:00.000000");
        CanonicalValue.Timestamp(rows[4]!.Value, 6).ShouldBe("2026-11-04T23:59:59.999999");
    }

    [Fact]
    public async Task Db2_hour_24_timestamps_cannot_be_read_as_DateTime_so_the_extract_fails_loudly()
    {
        await using var c = await Env.KeyStone.OpenAsync(cancellationToken: Ct);
        using var command = new DB2Command("SELECT TIMESTAMP('2026-11-04-24.00.00.000000') FROM SYSIBM.SYSDUMMY1", c);
        using var reader = (DB2DataReader)await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();

        Should.Throw<InvalidCastException>(() => reader.GetDateTime(0));
        reader.GetDB2TimeStamp(0).ToString().ShouldBe("2026-11-04-24.00.00.000000");
    }

    [Fact]
    public async Task Db2_database_is_utf8_and_varchar_lengths_count_bytes()
    {
        await using var c = await Env.KeyStone.OpenAsync(cancellationToken: Ct);
        using var cfg = new DB2Command("SELECT NAME, VALUE FROM SYSIBMADM.DBCFG WHERE NAME IN ('codeset', 'string_units')", c);
        using var reader = (DB2DataReader)await cfg.ExecuteReaderAsync(Ct);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(Ct))
        {
            values[reader.GetString(0)] = reader.GetString(1).Trim();
        }

        values["codeset"].ShouldBe("utf-8", StringCompareShould.IgnoreCase);
        values["string_units"].ShouldBe("SYSTEM");
        using var length = new DB2Command("SELECT LENGTH(V) FROM HARNESS.TYPEPROBE WHERE ID = 4", c);
        Convert.ToInt32(await length.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(28, "17 characters, 28 UTF-8 bytes");
    }

    [Fact]
    public async Task Sql_bulk_copy_keeps_31_digit_decimals_as_SqlDecimal_while_GetDecimal_overflows()
    {
        await using var connection = await ReportingDatabase.OpenAsync(Env.Reporting.AdminConnectionString, Ct);
        await ExecAsync(connection, "CREATE TABLE #d (V decimal(31,8) NULL)");
        using var table = new DataTable();
        table.Columns.Add("V", typeof(SqlDecimal));
        table.Rows.Add(SqlDecimal.Parse(Max31));
        table.Rows.Add(SqlDecimal.Parse(Mixed31));
        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "#d" })
        {
            await bulk.WriteToServerAsync(table, Ct);
        }

        await using var command = new SqlCommand("SELECT V FROM #d ORDER BY V DESC", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        reader.GetSqlDecimal(0).ToString().ShouldBe(Max31);
        Should.Throw<OverflowException>(() => reader.GetDecimal(0));
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        reader.GetSqlDecimal(0).ToString().ShouldBe(Mixed31);
    }

    [Fact]
    public async Task Sql_free_text_columns_keep_text_outside_windows_1252_only_with_the_utf8_collation_of_migration_0007()
    {
        await using var connection = await ReportingDatabase.OpenAsync(Env.Reporting.AdminConnectionString, Ct);
        await ExecAsync(connection, "CREATE TABLE #t (Latin varchar(60) NULL)");
        using var table = new DataTable();
        table.Columns.Add("Latin", typeof(string));
        table.Rows.Add(NonLatin1);
        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "#t" })
        {
            await bulk.WriteToServerAsync(table, Ct);
        }

        await using (var latin = new SqlCommand("SELECT Latin FROM #t", connection))
        {
            ((string)(await latin.ExecuteScalarAsync(Ct))!).ShouldBe("Café Zoë – O ? ??", "the default collation silently best-fits");
        }

        await using var collation = new SqlCommand(
            "SELECT COUNT(*) FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id " +
            "WHERE c.collation_name = 'Latin1_General_100_CI_AS_SC_UTF8' AND SCHEMA_NAME(t.schema_id) + '.' + t.name + '.' + c.name IN " +
            "('stg.KsAccount.DESCRIPTION', 'stg.KsPostedTxn.DESCRIPTION', 'stg.KsGlEntry.MEMO', 'core.Account.Description', 'core.PostedTransaction.Description', 'core.GlEntry.Memo')",
            connection);
        ((int)(await collation.ExecuteScalarAsync(Ct))!).ShouldBe(6);
    }

    [Fact]
    public async Task Sql_comparisons_ignore_trailing_spaces_and_case_which_limits_merge_change_detection()
    {
        await using var connection = await ReportingDatabase.OpenAsync(Env.Reporting.AdminConnectionString, Ct);
        await using var command = new SqlCommand(
            "SELECT CASE WHEN EXISTS (SELECT CAST('abc' AS varchar(10)) EXCEPT SELECT CAST('ABC   ' AS varchar(10))) THEN 'differs' ELSE 'same' END", connection);
        ((string)(await command.ExecuteScalarAsync(Ct))!).ShouldBe("same", "ADR 0005 records this as a known limitation of the merge procedures");
    }

    private async Task<Dictionary<int, T>> ReadAsync<T>(Func<DB2DataReader, T> read)
    {
        await using var c = await Env.KeyStone.OpenAsync(cancellationToken: Ct);
        using var command = new DB2Command("SELECT ID, C3, C10, V, D31, TS FROM HARNESS.TYPEPROBE ORDER BY ID", c);
        using var reader = (DB2DataReader)await command.ExecuteReaderAsync(Ct);
        var result = new Dictionary<int, T>();
        while (await reader.ReadAsync(Ct))
        {
            result[reader.GetInt32(0)] = read(reader);
        }

        return result;
    }

    private static async Task InsertAsync(DB2Connection c, int id, string? c3, string? c10, string? v, string? d31, DateTime? ts)
    {
        using var insert = new DB2Command("INSERT INTO HARNESS.TYPEPROBE (ID, C3, C10, V, D31, TS) VALUES (?, ?, ?, ?, ?, ?)", c);
        var p = Enumerable.Range(0, 6).Select(_ => insert.Parameters.Add(insert.CreateParameter())).ToArray();
        Db2Values.Bind(p[0], id.ToString(System.Globalization.CultureInfo.InvariantCulture), ColumnType.Int);
        Db2Values.Bind(p[1], c3, ColumnType.Char(3));
        Db2Values.Bind(p[2], c10, ColumnType.Char(10));
        Db2Values.Bind(p[3], v, ColumnType.VarChar(60));
        Db2Values.Bind(p[4], d31, ColumnType.Decimal(31, 8));
        Db2Values.Bind(p[5], ts is null ? null : CanonicalValue.Timestamp(ts.Value, 6), ColumnType.DateTime2(6));
        await insert.ExecuteNonQueryAsync(Ct);
    }

    private static async Task ExecAsync(DB2Connection c, string sql)
    {
        using var command = new DB2Command(sql, c);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task ExecAsync(SqlConnection c, string sql)
    {
        await using var command = new SqlCommand(sql, c);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task TryAsync(DB2Connection c, string sql)
    {
        try
        {
            await ExecAsync(c, sql);
        }
        catch (DB2Exception)
        {
            // The probe table did not exist.
        }
    }
}
