using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using CreditUnion.Batch.AcceptanceTests.Harness;
using CreditUnion.Batch.AcceptanceTests.Support;
using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.Core.Master;
using CreditUnion.Batch.Core.Pipelines;
using CreditUnion.Batch.KeyStone.Master;
using CreditUnion.Batch.TestData.Canonical;
using CreditUnion.Batch.TestData.Golden;
using CreditUnion.Batch.TestData.KeyStone;
using CreditUnion.Batch.TestData.Oracle;
using CreditUnion.Batch.TestHarness;
using CreditUnion.Batch.TestHarness.Faults;
using CreditUnion.Batch.TestHarness.Logging;
using CreditUnion.Batch.TestHarness.Scenarios;
using CreditUnion.Batch.TestHarness.Sql;
using CreditUnion.Batch.TestHarness.Stubs;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CreditUnion.Batch.AcceptanceTests.Master;

/// <summary>
/// Conversion checks for PL_Master_NightlyBatch beyond AT-14 to AT-18 (ADR 0014, ADR 0015), against the real containers: a child
/// that fails in an activity (no ADF error code) through the master, with the AT-17 rules applied to its logs and failure details;
/// the evidence file against the procedure; and the evidence sink's all-or-nothing commit on Azurite.
/// </summary>
public sealed class NightlyBatchConversionTests(BatchEnvironmentFixture fixture)
{
    private BatchEnvironment Env => fixture.Environment;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_master_whose_Extract_cannot_reach_Db2_fails_at_Extract_with_the_root_cause_lets_Card_finish_and_logs_nothing_sensitive()
    {
        var scenario = Scenario.Load("standard-day");
        await Env.LoadAsync(scenario, Ct);
        await using var proxy = FaultInjectingTcpProxy.Start(Env.KeyStone.Host, Env.KeyStone.Port);
        await using var host = BatchHost.Start(Env, scenario, Env.KeyStone.ExtractConnectionStringVia(FaultInjectingTcpProxy.Host, proxy.Port));
        proxy.RefuseNextConnections(1000);
        Guid[] runIds = [Guid.NewGuid()];

        var run = await host.RunAsync(scenario.Runs[0], runIds[0]);

        run.ShouldHaveFailedWith(PipelineErrorCodes.BatchStepFailed);
        run.Result.Failure!.ErrorMessage.ShouldStartWith("BATCH_STEP_FAILED: PL_ExtractKeyStone failed: ");
        var events = await MasterEventsAsync(runIds[0]);
        events.Select(e => e.Status).ShouldBe(["Started", "Failed"]);
        var failed = events[1];
        failed.StepName.ShouldBe(PipelineNames.Extract);
        failed.ErrorCode.ShouldBe("DB2Exception", "no ADF code exists, so the Failed event names the child's root cause (ADR 0014)");
        failed.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        host.Trace.SubOrchestrationsOf(run.Result.InstanceId).Select(a => a.Name).Distinct().ShouldBe([PipelineNames.Extract, PipelineNames.CardSettlement], ignoreOrder: true);
        (await StatusesAsync(runIds[0], PipelineNames.CardSettlement)).ShouldContain("Succeeded", "the parallel sibling ran to its end");
        (await Env.Blobs.ListAsync(Ct)).ShouldNotContain(b => b.Container == BatchOracle.EvidenceContainer, "no evidence for a failed run");

        await Idempotency.RerunShouldChangeNothingAsync(host, runIds, [run], async () => [await host.RunAsync(scenario.Runs[0], Guid.NewGuid())]);
        var findings = host.Logs.Findings(Secrets(host), FullNumbers(scenario));
        findings.ShouldBeEmpty($"{findings.Count} log line(s), span(s) or failure detail(s) expose sensitive values");
        host.Logs.Logs.ShouldContain(l => l.Message.Contains("step PL_ExtractKeyStone failed", StringComparison.Ordinal), "the master logs the failed step; an empty log would pass vacuously");
    }

    [Fact]
    public async Task The_evidence_file_is_the_procedures_result_set_in_its_order_with_retain_until_metadata()
    {
        var scenario = Scenario.Load("recon-card-off-by-one-cent");
        await Env.LoadAsync(scenario, Ct);
        await using var host = BatchHost.Start(Env, scenario);
        var runId = Guid.NewGuid();

        (await host.RunAsync(scenario.Runs[0], runId)).ShouldHaveSucceeded();

        var blob = (await Env.Blobs.ListAsync(Ct)).Single(b => b.Container == BatchOracle.EvidenceContainer);
        blob.Name.ShouldBe(RunEvidenceFile.BlobName(scenario.Runs[0].Date(), runId));
        blob.Metadata.ShouldBe(new Dictionary<string, string> { ["retain_until"] = "TBD" });
        blob.Content.Take(3).ShouldNotBe(new byte[] { 0xEF, 0xBB, 0xBF }, "UTF-8 without a byte-order mark");
        var written = Entries(blob.Content);
        var procedure = await ProcedureRowsAsync(runId, scenario.Runs[0].Date());

        // The export ran before Log Batch Succeeded, and generatedUtc is the procedure's own clock: everything else is identical, in order.
        procedure.RemoveAll(e => e.Section == "BatchRun" && e.Item.StartsWith(PipelineNames.Master, StringComparison.Ordinal) && e.Value!.StartsWith("Succeeded", StringComparison.Ordinal));
        static RunEvidenceEntry WithoutClock(RunEvidenceEntry e) => e.Item == "generatedUtc" ? e with { Value = "-" } : e;
        written.Select(WithoutClock).ShouldBe(procedure.Select(WithoutClock));
        written.Select(e => e.Section).Distinct().ShouldBe(["Run", "BatchRun", "Files", "RowCounts", "Checksums", "Reconciliation", "Exceptions"]);
        written.Single(e => e.Item == "batchRunId").Value.ShouldBe(runId.ToString("D").ToUpperInvariant(), "the procedure prints the uniqueidentifier");
    }

    [Fact]
    public async Task The_evidence_sink_commits_only_complete_content_replaces_a_file_whole_and_writes_several_blocks()
    {
        await Env.Blobs.ResetAsync(Ct);
        var store = new AzureEvidenceBlobStore(Options.Create(new BlobStorageOptions { ConnectionString = Env.Blobs.ConnectionString }));
        var container = new BlobServiceClient(Env.Blobs.ConnectionString).GetBlobContainerClient(BatchOracle.EvidenceContainer);
        var metadata = RunEvidenceFile.Metadata("TBD");
        const string Name = "2026-11-04/batch-run-test.json";

        // A writer that fails part-way leaves no blob at all.
        await Should.ThrowAsync<IOException>(() => store.WriteAsync(BatchOracle.EvidenceContainer, Name, metadata, RunEvidenceFile.ContentType, async (stream, ct) =>
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("[{\"section\":\"Run\""), ct);
            throw new IOException("the source failed");
        }, Ct));
        (await container.GetBlobClient(Name).ExistsAsync(Ct)).Value.ShouldBeFalse("nothing is committed when the content is incomplete");

        // A complete write commits content, metadata and content type together.
        var first = Encoding.UTF8.GetBytes("[]");
        await store.WriteAsync(BatchOracle.EvidenceContainer, Name, metadata, RunEvidenceFile.ContentType, (stream, ct) => stream.WriteAsync(first, ct).AsTask(), Ct);
        var properties = (await container.GetBlobClient(Name).GetPropertiesAsync(cancellationToken: Ct)).Value;
        (properties.ContentType, properties.Metadata["retain_until"], properties.ContentLength).ShouldBe(("application/json", "TBD", 2L));

        // A failed retry of the same run's export leaves the earlier file untouched; a complete one replaces it whole.
        await Should.ThrowAsync<IOException>(() => store.WriteAsync(BatchOracle.EvidenceContainer, Name, RunEvidenceFile.Metadata("2033-12-31"), RunEvidenceFile.ContentType, async (stream, ct) =>
        {
            await stream.WriteAsync(new byte[1000], ct);
            throw new IOException("the source failed");
        }, Ct));
        (await Download(container, Name)).ShouldBe(first);
        (await container.GetBlobClient(Name).GetPropertiesAsync(cancellationToken: Ct)).Value.Metadata["retain_until"].ShouldBe("TBD");

        var large = RandomNumberGenerator.GetBytes((AzureEvidenceBlobStore.BlockSize * 2) + 12345);
        await store.WriteAsync(BatchOracle.EvidenceContainer, Name, RunEvidenceFile.Metadata("2033-12-31"), RunEvidenceFile.ContentType, async (stream, ct) =>
        {
            for (var offset = 0; offset < large.Length; offset += 64 * 1024)
            {
                await stream.WriteAsync(large.AsMemory(offset, Math.Min(64 * 1024, large.Length - offset)), ct);
            }
        }, Ct);
        (await Download(container, Name)).ShouldBe(large, "three staged blocks, committed in order");
        (await container.GetBlobClient(Name).GetPropertiesAsync(cancellationToken: Ct)).Value.Metadata["retain_until"].ShouldBe("2033-12-31");
        await Env.Blobs.ResetAsync(Ct);
    }

    private static async Task<byte[]> Download(BlobContainerClient container, string name) =>
        (await container.GetBlobClient(name).DownloadContentAsync(Ct)).Value.Content.ToArray();

    private static List<RunEvidenceEntry> Entries(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(e => new RunEvidenceEntry(e.GetProperty("section").GetString()!, e.GetProperty("item").GetString()!, e.GetProperty("value").GetString()))];
    }

    private async Task<List<RunEvidenceEntry>> ProcedureRowsAsync(Guid runId, DateOnly date)
    {
        await using var connection = await ReportingDatabase.OpenAsync(Env.Reporting.AdminConnectionString, Ct);
        await using var command = new SqlCommand("ops.usp_GetRunEvidence", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add(new SqlParameter("@BatchRunId", SqlDbType.UniqueIdentifier) { Value = runId });
        command.Parameters.Add(new SqlParameter("@BusinessDate", SqlDbType.Date) { Value = date.ToDateTime(TimeOnly.MinValue) });
        var rows = new List<RunEvidenceEntry>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new RunEvidenceEntry(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return rows;
    }

    private async Task<List<(string Status, string? StepName, string? ErrorCode, string? ErrorMessage)>> MasterEventsAsync(Guid runId)
    {
        await using var connection = await ReportingDatabase.OpenAsync(Env.Reporting.AdminConnectionString, Ct);
        await using var command = new SqlCommand(
            "SELECT Status, StepName, ErrorCode, ErrorMessage FROM ops.BatchRun WHERE BatchRunId = @r AND PipelineName = @p ORDER BY BatchRunEventId", connection);
        command.Parameters.Add(new SqlParameter("@r", SqlDbType.UniqueIdentifier) { Value = runId });
        command.Parameters.Add(new SqlParameter("@p", SqlDbType.VarChar, 100) { Value = PipelineNames.Master });
        var events = new List<(string, string?, string?, string?)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            events.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return events;
    }

    private async Task<List<string>> StatusesAsync(Guid runId, string pipeline)
    {
        await using var connection = await ReportingDatabase.OpenAsync(Env.Reporting.AdminConnectionString, Ct);
        await using var command = new SqlCommand("SELECT Status FROM ops.BatchRun WHERE BatchRunId = @r AND PipelineName = @p", connection);
        command.Parameters.Add(new SqlParameter("@r", SqlDbType.UniqueIdentifier) { Value = runId });
        command.Parameters.Add(new SqlParameter("@p", SqlDbType.VarChar, 100) { Value = pipeline });
        var statuses = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            statuses.Add(reader.GetString(0));
        }

        return statuses;
    }

    private IEnumerable<SensitiveValue> Secrets(BatchHost host)
    {
        var s = Env.Secrets;
        yield return new("the Db2 instance-owner password", s.Db2AdminPassword);
        yield return new("the KSEXTRACT password", s.Db2ExtractPassword);
        yield return new("the SQL Server sa password", s.SqlAdminPassword);
        yield return new("the cu_batch_writer password", s.SqlWriterPassword);
        yield return new("the cu_report_reader password", s.SqlReaderPassword);
        yield return new("the KeyBridge password", s.KeyBridgePassword);
        yield return new("the KeyBridge Basic credential", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{KeyBridgeStub.Username}:{s.KeyBridgePassword}")));
        yield return new("the alert webhook token", s.AlertWebhookToken);
        yield return new("the alert webhook URL", host.Webhook.Url);
    }

    /// <summary>Every member, account and card number of the scenario (the AT-17 rule), labelled by its last four digits only.</summary>
    private static Dictionary<string, string> FullNumbers(Scenario scenario)
    {
        var numbers = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(TableData table, string column, string kind)
        {
            foreach (var value in table.Rows.Select(r => r[table.ColumnIndex(column)]).OfType<string>().Select(v => v.Trim()))
            {
                numbers.TryAdd(value, string.Create(CultureInfo.InvariantCulture, $"a full {kind} number (ending {value[^4..]})"));
            }
        }

        Add(scenario.InputTable(KeyStoneTables.Member), "MEMBER_NBR", "member");
        Add(scenario.InputTable(KeyStoneTables.Account), "ACCOUNT_NBR", "account");
        var cards = scenario.GoldenTable(GoldenTables.CardSettlement.Name);
        foreach (var row in cards.Rows)
        {
            var card = row[cards.ColumnIndex("CardBin")] + row[cards.ColumnIndex("CardLast4")];
            numbers.TryAdd(card, $"a full card number (ending {card[^4..]})");
        }

        numbers.Count.ShouldBeGreaterThan(100);
        return numbers;
    }
}
