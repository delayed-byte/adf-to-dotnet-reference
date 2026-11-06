using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreditUnion.Batch.TestData.Canonical;
using CreditUnion.Batch.TestData.Card;
using CreditUnion.Batch.TestData.Golden;
using CreditUnion.Batch.TestData.KeyBridge;
using CreditUnion.Batch.TestData.KeyStone;
using CreditUnion.Batch.TestData.Oracle;
using CreditUnion.Batch.TestData.Scenarios;
using CreditUnion.Batch.TestHarness.Golden;
using CreditUnion.Batch.TestHarness.Scenarios;
using CreditUnion.Batch.TestHarness.Sql;
using Microsoft.Data.SqlClient;

namespace CreditUnion.Batch.AcceptanceTests.Harness;

/// <summary>
/// Cross-check of the golden outputs (not an implementation of the pipelines): feeds a scenario's
/// inputs straight into the Phase 1 stored procedures, which are part of the ADF source of truth, in
/// the order the pipelines call them, and collects every place where a procedure disagrees with the
/// oracle. The data flow has no procedure (its .NET form is decided in Phase 6), so DailyGlTotals are
/// computed by an independent T-SQL statement of the same rule. No Db2 reads, blob moves, HTTP calls,
/// retries or orchestration happen here.
/// </summary>
internal sealed class ProcedureReplay(ReportingDatabase database, Scenario scenario, IReadOnlyList<Guid> runIds)
{
    private static readonly ColumnDefinition[] RawColumns =
        [new("RecordType", ColumnType.NVarChar(10), true), .. Enumerable.Range(1, 9).Select(i => new ColumnDefinition($"Field{i:D2}", ColumnType.NVarChar(200), true))];

    private readonly Dictionary<string, byte[]> landing = scenario.InputBlobs()
        .Where(b => b.Container == BatchOracle.LandingContainer).ToDictionary(b => b.Name, b => b.Content, StringComparer.Ordinal);

    private readonly GlMappingResponse mapping = GlMappingResponse.Parse(scenario.KeyBridgeMappingJson);
    private readonly RunTokens tokens = new(runIds);

    public List<string> Problems { get; } = [];

    public async Task RunAsync(RunInput run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var date = CanonicalValue.ParseDate(run.BusinessDate);
        var runId = runIds[run.Run - 1];
        var expected = scenario.ExpectedRun(run.Run);
        IReadOnlyList<string> steps = run.Pipeline == Pipelines.Master ? Pipelines.Children : [run.Pipeline];
        foreach (var step in steps)
        {
            var succeeded = step switch
            {
                Pipelines.Extract => await ExtractAsync(date, runId, ct),
                Pipelines.Card => await CardAsync(date, runId, run.Run, ct),
                Pipelines.Map => await MapAsync(date, runId, expected, ct),
                Pipelines.Recon => await ReconAsync(date, runId, run, expected, ct),
                _ => throw new InvalidOperationException(step),
            };
            if (!succeeded)
            {
                return;
            }
        }

        if (run.Pipeline == Pipelines.Master && expected.Evidence is { } evidenceFile)
        {
            var rows = await QueryAsync("ops.usp_GetRunEvidence", ct, ("@BatchRunId", runId), ("@BusinessDate", date));
            var actual = new JsonArray([.. rows.Select(r => (JsonNode)new JsonObject { ["section"] = (string?)r["section"], ["item"] = (string?)r["item"], ["value"] = (string?)r["value"] })]);
            Problems.AddRange(GoldenAssert.EvidenceDifferences(scenario.GoldenText(evidenceFile), actual.ToJsonString(), tokens).Select(d => $"run {run.Run}: {d}"));
        }
    }

    // PL_ExtractKeyStone: TRUNCATE + copy the date's source rows into staging, then core.usp_MergeKeyStone.
    private async Task<bool> ExtractAsync(DateOnly date, Guid runId, CancellationToken ct)
    {
        foreach (var (target, table, dateColumn) in new[]
                 {
                     ("stg.KsPostedTxn", KeyStoneTables.PostedTxn, "POSTING_DATE"),
                     ("stg.KsAccount", KeyStoneTables.Account, "LAST_ACTIVITY_DATE"),
                     ("stg.KsGlEntry", KeyStoneTables.GlEntry, "POSTING_DATE"),
                 })
        {
            await ExecSqlAsync($"TRUNCATE TABLE {target}", ct);
            await database.BulkInsertAsync(target, table.Columns, scenario.InputTable(table).Where(dateColumn, CanonicalValue.Date(date)), cancellationToken: ct);
        }

        await ExecAsync("core.usp_MergeKeyStone", ct, ("@BusinessDate", date), ("@BatchRunId", runId));
        return true;
    }

    // PL_IngestCardSettlement: land each matching file raw, validate it, promote or quarantine it, then merge.
    private async Task<bool> CardAsync(DateOnly date, Guid runId, int run, CancellationToken ct)
    {
        await ExecAsync("stg.usp_ResetCardSettlementStaging", ct, ("@BusinessDate", date));
        var folder = CanonicalValue.Date(date) + "/";
        var files = landing.Keys.Where(n => n.StartsWith(folder, StringComparison.Ordinal)).Select(n => n[folder.Length..])
            .Where(f => !f.Contains('/', StringComparison.Ordinal) && f.StartsWith("settlement_", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToList();

        foreach (var file in files)
        {
            var raw = SettlementValidator.Land(landing[folder + file]);
            await database.BulkInsertAsync("stg.CardSettlementRaw", RawColumns,
                new TableData("raw", [.. RawColumns.Select(c => c.Name)], raw.Select(r => (IReadOnlyList<string?>)[r.RecordType, .. r.Fields])),
                new Dictionary<string, object> { ["BusinessDate"] = date.ToDateTime(TimeOnly.MinValue), ["SourceFile"] = file }, ct);

            var proc = (await QueryAsync("stg.usp_ValidateCardSettlementFile", ct, ("@BusinessDate", date), ("@SourceFile", file), ("@BatchRunId", runId))).Single();
            var port = SettlementValidator.Validate(raw, date);
            Compare($"{file} IsValid", port.IsValid, (bool)proc["IsValid"]!);
            Compare($"{file} DetailRowCount", port.DetailRowCount, (int)proc["DetailRowCount"]!);
            Compare($"{file} TrailerRowCount", port.TrailerRowCount, (int?)proc["TrailerRowCount"]);
            Compare($"{file} DetailTotalAmount", port.DetailTotalAmount, (decimal?)proc["DetailTotalAmount"]);
            Compare($"{file} TrailerTotalAmount", port.TrailerTotalAmount, (decimal?)proc["TrailerTotalAmount"]);
            Compare($"{file} ErrorSummary", port.ErrorSummary, (string?)proc["ErrorSummary"]);

            var destination = (port.IsValid ? BatchOracle.ArchiveContainer : BatchOracle.QuarantineContainer) + "/" + folder + file;
            if ((bool)proc["IsValid"]!)
            {
                await ExecAsync("stg.usp_PromoteCardSettlementFile", ct, ("@BusinessDate", date), ("@SourceFile", file));
                await ExecAsync("ops.usp_LogCardFile", ct, ("@BatchRunId", runId), ("@BusinessDate", date), ("@SourceFile", file), ("@Outcome", "Archived"),
                    ("@DetailRowCount", proc["DetailRowCount"]), ("@TrailerRowCount", proc["TrailerRowCount"]), ("@DetailTotalAmount", proc["DetailTotalAmount"]),
                    ("@TrailerTotalAmount", proc["TrailerTotalAmount"]), ("@DestinationPath", destination));
            }
            else
            {
                var errors = await QueryAsync("stg.usp_GetCardSettlementValidationErrors", ct, ("@BusinessDate", date), ("@SourceFile", file));
                var manifest = new JsonArray([.. errors.Select(e => (JsonNode)new JsonObject(e.Select(kv => KeyValuePair.Create(kv.Key, kv.Value is null ? null : (JsonNode?)JsonValue.Create((string)kv.Value)))))]);
                var golden = scenario.Golden.Blobs.SingleOrDefault(b => b.Container == BatchOracle.QuarantineContainer && b.Name == folder + file + ".errors.json");
                if (golden?.GoldenFile is null)
                {
                    Problems.Add($"run {run}: the procedure quarantines {file}, the golden output does not");
                }
                else
                {
                    Problems.AddRange(JsonEquivalence.Differences(scenario.GoldenText(golden.GoldenFile), tokens.Tokenize(manifest.ToJsonString()), unorderedArrays: true)
                        .Select(d => $"{file} manifest {d}"));
                }

                await ExecAsync("ops.usp_LogCardFile", ct, ("@BatchRunId", runId), ("@BusinessDate", date), ("@SourceFile", file), ("@Outcome", "Quarantined"),
                    ("@DetailRowCount", proc["DetailRowCount"]), ("@TrailerRowCount", proc["TrailerRowCount"]), ("@ErrorSummary", proc["ErrorSummary"]),
                    ("@DestinationPath", destination));
            }

            landing.Remove(folder + file);
        }

        await ExecAsync("core.usp_MergeCardSettlement", ct, ("@BusinessDate", date), ("@BatchRunId", runId));
        return true;
    }

    // PL_MapAndAggregate: cache the KeyBridge mapping, check for unmapped codes, then the data flow rule in T-SQL.
    private async Task<bool> MapAsync(DateOnly date, Guid runId, RunExpectation expected, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(scenario.KeyBridgeMappingJson);
        await ExecAsync("ref.usp_UpsertTxnCodeGlMap", ct, ("@MappingJson", document.RootElement.GetProperty("mappings").GetRawText()),
            ("@SourceVersion", mapping.Version), ("@BatchRunId", runId));

        var unmapped = (await QueryAsync("ref.usp_GetUnmappedTxnCodes", ct, ("@BusinessDate", date))).Single();
        var expectedFailure = expected.Messages.SingleOrDefault(m => m.Pipeline == Pipelines.Map);
        if ((int)unmapped["UnmappedCount"]! > 0)
        {
            var message = "Unmapped transaction codes: " + (string)unmapped["UnmappedCodes"]!;
            Compare("Map failure message", expectedFailure?.Contains, message);
            return false;
        }

        Compare("Map failure message", expectedFailure?.Contains, null);
        await ExecSqlAsync(DailyGlTotalsSql, ct, ("@BusinessDate", date));
        return true;
    }

    // PL_Reconciliation: lookups, record exceptions, alert if due.
    private async Task<bool> ReconAsync(DateOnly date, Guid runId, RunInput run, RunExpectation expected, CancellationToken ct)
    {
        var tolerance = CanonicalValue.ParseDecimal(run.Tolerance);
        var card = (await QueryAsync("ops.usp_GetCardReconTotals", ct, ("@BusinessDate", date), ("@Tolerance", tolerance))).Single();
        var gl = (await QueryAsync("ops.usp_GetGlReconTotals", ct, ("@BusinessDate", date), ("@Tolerance", tolerance))).Single();
        var record = (await QueryAsync("ops.usp_RecordReconExceptions", ct, ("@BusinessDate", date), ("@BatchRunId", runId), ("@Tolerance", tolerance))).Single();

        var outcome = scenario.GoldenTable(GoldenTables.BatchRun.Name).Rows.Single(r => r[0] == run.Run.ToString(CultureInfo.InvariantCulture)
            && r[2] == Pipelines.Recon && r[4] is "Reconciled" or "Exceptions");
        Compare("Recon status", outcome[4], (bool)record["IsOutOfTolerance"]! ? "Exceptions" : "Reconciled");
        Compare("Recon notes", outcome[8],
            $"card difference={CanonicalValue.Decimal((decimal)card["Difference"]!, 2)}; GL items out of tolerance={gl["ItemsOutOfTolerance"]}; tolerance={run.Tolerance}");
        Compare("Alert required", expected.Alerts.Count > 0, (bool)record["AlertRequired"]!);
        if ((bool)record["AlertRequired"]!)
        {
            using var alert = JsonDocument.Parse(scenario.GoldenText(expected.Alerts[0]));
            Compare("Alert summary", alert.RootElement.GetProperty("summary").GetString(), (string)record["AlertSummary"]!);
            Compare("Alert card difference", alert.RootElement.GetProperty("cardDifference").GetDecimal(), (decimal)card["Difference"]!);
            await ExecAsync("ops.usp_MarkReconAlertSent", ct, ("@BusinessDate", date), ("@BatchRunId", runId));
        }

        return true;
    }

    // DF_MapAndAggregateGl restated in T-SQL: banker's rounding of each allocation (ROUND(x, 2, 1) truncates;
    // an exact half cent goes to the even cent), then sums per GL account, replacing the date's rows.
    private const string DailyGlTotalsSql = """
        DELETE FROM core.DailyGlTotals WHERE BusinessDate = @BusinessDate;
        WITH a AS (
            SELECT t.TxnId, m.GlAccountNbr, m.DrCr, t.Amount * m.AllocationRatio AS x
            FROM core.PostedTransaction AS t JOIN ref.TxnCodeGlMap AS m ON m.TxnCode = t.TxnCode
            WHERE t.PostingDate = @BusinessDate),
        r AS (
            SELECT TxnId, GlAccountNbr, DrCr,
                   CASE WHEN ABS(x - ROUND(x, 2, 1)) = 0.005
                        THEN ROUND(x, 2, 1) + CASE WHEN CAST(ABS(ROUND(x, 2, 1)) * 100 AS bigint) % 2 = 0 THEN 0 ELSE SIGN(x) * 0.01 END
                        ELSE ROUND(x, 2) END AS Amount
            FROM a)
        INSERT INTO core.DailyGlTotals (BusinessDate, GlAccountNbr, DebitTotal, CreditTotal, NetAmount, TxnCount)
        SELECT @BusinessDate, GlAccountNbr,
               SUM(CASE WHEN DrCr = 'D' THEN Amount ELSE 0 END),
               SUM(CASE WHEN DrCr = 'C' THEN Amount ELSE 0 END),
               SUM(CASE WHEN DrCr = 'D' THEN Amount ELSE -Amount END),
               COUNT(DISTINCT TxnId)
        FROM r GROUP BY GlAccountNbr;
        """;

    private void Compare<T>(string what, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Problems.Add($"{what}: oracle {expected?.ToString() ?? "null"}, procedure {actual?.ToString() ?? "null"}");
        }
    }

    private async Task ExecAsync(string procedure, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await ReportingDatabase.OpenAsync(database.AdminConnectionString, ct);
        await using var command = Command(connection, procedure, CommandType.StoredProcedure, parameters);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task ExecSqlAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await ReportingDatabase.OpenAsync(database.AdminConnectionString, ct);
        await using var command = Command(connection, sql, CommandType.Text, parameters);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<List<Dictionary<string, object?>>> QueryAsync(string procedure, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await ReportingDatabase.OpenAsync(database.AdminConnectionString, ct);
        await using var command = Command(connection, procedure, CommandType.StoredProcedure, parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i), StringComparer.Ordinal));
        }

        return rows;
    }

    private static SqlCommand Command(SqlConnection connection, string text, CommandType type, (string Name, object? Value)[] parameters)
    {
        var command = new SqlCommand(text, connection) { CommandType = type };
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(value switch
            {
                DateOnly d => new SqlParameter(name, SqlDbType.Date) { Value = d.ToDateTime(TimeOnly.MinValue) },
                Guid g => new SqlParameter(name, SqlDbType.UniqueIdentifier) { Value = g },
                decimal m => new SqlParameter(name, SqlDbType.Decimal) { Precision = 19, Scale = 2, Value = m },
                null => new SqlParameter(name, DBNull.Value),
                _ => new SqlParameter(name, value),
            });
        }

        return command;
    }
}
