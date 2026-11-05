using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreditUnion.Batch.TestData.Card;
using CreditUnion.Batch.TestData.Golden;
using CreditUnion.Batch.TestData.KeyBridge;
using CreditUnion.Batch.TestData.KeyStone;
using CreditUnion.Batch.TestData.Reporting;
using static CreditUnion.Batch.TestData.Canonical.CanonicalValue;

namespace CreditUnion.Batch.TestData.Oracle;

/// <summary>One execution of a pipeline entry point in a scenario. <see cref="Run"/> is 1-based.</summary>
public sealed record RunSpec(int Run, string Pipeline, DateOnly BusinessDate, DateTimeOffset? TriggerTimeUtc = null, string Tolerance = "0.00");

public sealed record EvidenceEntry(string Section, string Item, string Value);

public sealed record MessageExpectation(string Pipeline, string? StepName, string Contains);

public sealed record RunOutcome(
    RunSpec Spec,
    string Status,
    string? ErrorCode,
    string? FailedStep,
    IReadOnlyList<string> PipelinesRun,
    IReadOnlyList<string> PipelinesNotRun,
    IReadOnlyList<MessageExpectation> Messages,
    int KeyBridgeRequests,
    IReadOnlyList<string> Alerts,
    IReadOnlyList<EvidenceEntry>? Evidence);

/// <summary>
/// Executable statement of what the five ADF pipelines in <c>adf/</c> do, applied to an in-memory
/// model of the reporting store, blob storage and the alert webhook (ADR 0006). It follows the ADF
/// activities and the Phase 1 procedures step by step (merge rules, validation, logging, evidence),
/// but performs no I/O. Golden outputs are its final state after the scenario's runs. Where ADF
/// behaviour could not be observed, the choice made here is listed in ADR 0006 for human review.
/// </summary>
public sealed class BatchOracle
{
    /// <summary>Stands for a row's own BatchRunId inside ops.BatchRun golden rows (see GoldenTables.BatchRun).</summary>
    public const string RunIdToken = "{batchRunId}";
    public const string LandingContainer = "card-landing";
    public const string ArchiveContainer = "card-archive";
    public const string QuarantineContainer = "card-quarantine";
    public const string EvidenceContainer = "evidence";
    public const string RetainUntil = "TBD";
    public const string UtcToken = "<utc>";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Stands for the batch run ID of run <paramref name="run"/> in blob names, metadata, manifests and alerts.</summary>
    public static string RunToken(int run) => string.Create(CultureInfo.InvariantCulture, $"{{run:{run}}}");
    private static readonly string[] ExtractTargets = ["stg.KsPostedTxn", "stg.KsAccount", "stg.KsGlEntry"];

    private readonly KeyStoneDataset keyStone;
    private readonly GlMappingResponse keyBridge;
    private readonly SortedDictionary<string, BlobItem> blobs = new(StringComparer.Ordinal);
    private readonly SortedDictionary<long, PostedTxn> coreTransactions = [];
    private readonly SortedDictionary<string, Account> coreAccounts = new(StringComparer.Ordinal);
    private readonly SortedDictionary<long, GlEntry> coreGlEntries = [];
    private readonly SortedDictionary<string, CardSettlementRow> coreCard = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TxnCodeGlMapRow> glMap = [];
    private readonly List<DailyGlTotalRow> dailyGlTotals = [];
    private readonly List<ReconExceptionRow> reconExceptions = [];
    private readonly List<CardFileLogRow> cardFileLog = [];
    private readonly List<BatchRunEvent> batchRun = [];

    public BatchOracle(KeyStoneDataset keyStone, IEnumerable<BlobItem> initialBlobs, GlMappingResponse keyBridge)
    {
        ArgumentNullException.ThrowIfNull(initialBlobs);
        this.keyStone = keyStone ?? throw new ArgumentNullException(nameof(keyStone));
        this.keyBridge = keyBridge ?? throw new ArgumentNullException(nameof(keyBridge));
        foreach (var blob in initialBlobs)
        {
            blobs[blob.Path] = blob;
        }
    }

    public IEnumerable<PostedTxn> CoreTransactions => coreTransactions.Values;

    public IEnumerable<Account> CoreAccounts => coreAccounts.Values;

    public IEnumerable<GlEntry> CoreGlEntries => coreGlEntries.Values;

    public IEnumerable<CardSettlementRow> CoreCardSettlements => coreCard.Values;

    public IReadOnlyList<TxnCodeGlMapRow> GlMap => glMap;

    public IReadOnlyList<DailyGlTotalRow> DailyGlTotals => dailyGlTotals;

    public IReadOnlyList<ReconExceptionRow> ReconExceptions => reconExceptions;

    public IReadOnlyList<CardFileLogRow> CardFileLog => cardFileLog;

    public IReadOnlyList<BatchRunEvent> BatchRun => batchRun;

    public IEnumerable<BlobItem> Blobs => blobs.Values;

    public RunOutcome Run(RunSpec run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var context = new RunContext(run);
        if (run.Pipeline == Pipelines.Master)
        {
            Master(context);
        }
        else
        {
            var result = RunChild(context, run.Pipeline);
            if (!result.Succeeded)
            {
                context.Fail(result.ErrorCode, run.Pipeline);
            }
        }

        var notRun = Pipelines.Children.Prepend(Pipelines.Master)
            .Where(p => run.Pipeline == Pipelines.Master || p == run.Pipeline)
            .Except(context.PipelinesRun).ToList();
        return new RunOutcome(run, context.Status, context.ErrorCode, context.FailedStep, context.PipelinesRun, notRun,
            context.Messages, context.KeyBridgeRequests, context.Alerts, context.Evidence);
    }

    private ChildResult RunChild(RunContext context, string pipeline)
    {
        context.PipelinesRun.Add(pipeline);
        return pipeline switch
        {
            Pipelines.Extract => Extract(context),
            Pipelines.Card => IngestCard(context),
            Pipelines.Map => MapAndAggregate(context),
            Pipelines.Recon => Reconcile(context),
            _ => throw new ArgumentException($"Unknown pipeline {pipeline}.", nameof(pipeline)),
        };
    }

    // PL_Master_NightlyBatch: Extract and Card in parallel, then Map, then Reconciliation, then the
    // evidence export. A failing child is logged as Failed with its step name and error code, and the
    // master fails with BATCH_STEP_FAILED without starting later steps.
    private void Master(RunContext context)
    {
        context.PipelinesRun.Add(Pipelines.Master);
        Log(context, Pipelines.Master, null, "Started");

        var stage1 = new[] { Pipelines.Extract, Pipelines.Card }.Select(p => (Pipeline: p, Result: RunChild(context, p))).ToList();
        foreach (var (pipeline, result) in stage1.Where(s => !s.Result.Succeeded))
        {
            MasterStepFailed(context, pipeline, result);
        }

        if (stage1.Any(s => !s.Result.Succeeded))
        {
            return;
        }

        foreach (var pipeline in new[] { Pipelines.Map, Pipelines.Recon })
        {
            var result = RunChild(context, pipeline);
            if (!result.Succeeded)
            {
                MasterStepFailed(context, pipeline, result);
                return;
            }
        }

        var date = Date(context.Spec.BusinessDate);
        context.Evidence = BuildEvidence(context);
        PutBlob(EvidenceContainer, $"{date}/batch-run-{RunToken(context.Spec.Run)}.json", [], new Dictionary<string, string> { ["retain_until"] = RetainUntil });
        Log(context, Pipelines.Master, null, "Succeeded", objectName: $"evidence/{date}/batch-run-{RunIdToken}.json");
    }

    private void MasterStepFailed(RunContext context, string pipeline, ChildResult result)
    {
        Log(context, Pipelines.Master, pipeline, "Failed", errorCode: result.ErrorCode);
        if (result.Message is not null)
        {
            context.Messages.Add(new MessageExpectation(Pipelines.Master, pipeline, result.Message));
        }

        context.Fail("BATCH_STEP_FAILED", pipeline);
    }

    // PL_ExtractKeyStone + core.usp_MergeKeyStone. Rows whose date column equals the business date
    // are copied verbatim (ADR 0005) and merged on their source keys.
    private ChildResult Extract(RunContext context)
    {
        var date = context.Spec.BusinessDate;
        Log(context, Pipelines.Extract, null, "Started");

        var txns = keyStone.Transactions.Where(t => t.PostingDate == date).ToList();
        var accounts = keyStone.Accounts.Where(a => a.LastActivityDate == date).ToList();
        var glEntries = keyStone.GlEntries.Where(g => g.PostingDate == date).ToList();
        foreach (var (target, count) in ExtractTargets.Zip([txns.Count, accounts.Count, glEntries.Count]))
        {
            Log(context, Pipelines.Extract, "CopyToStaging", "Succeeded", objectName: target, recordCount: count);
        }

        var (accIns, accUpd) = Merge(coreAccounts, accounts, a => a.AccountNbr, (s, t) => s.LastMaintTs >= t.LastMaintTs);
        var (txnIns, txnUpd) = Merge(coreTransactions, txns, t => t.TxnId, (_, _) => true);
        var (glIns, glUpd) = Merge(coreGlEntries, glEntries, g => g.GlEntryId, (_, _) => true);
        LogMerge(context, Pipelines.Extract, "MergeKeyStone", "core.Account", accounts.Count, accIns, accUpd);
        LogMerge(context, Pipelines.Extract, "MergeKeyStone", "core.PostedTransaction", txns.Count, txnIns, txnUpd);
        LogMerge(context, Pipelines.Extract, "MergeKeyStone", "core.GlEntry", glEntries.Count, glIns, glUpd);

        Log(context, Pipelines.Extract, null, "Succeeded", recordCount: ExtractTargets.Length,
            notes: "Tables extracted (see CopyToStaging and MergeKeyStone events for row counts).");
        return ChildResult.Ok;
    }

    // PL_IngestCardSettlement: list card-landing/{date}/ (non-recursive; folders are skipped), keep
    // settlement_*.csv, validate each file, promote and archive valid files, write an error manifest
    // and quarantine invalid ones, then merge staging into core.CardSettlement.
    private ChildResult IngestCard(RunContext context)
    {
        var date = context.Spec.BusinessDate;
        var folder = Date(date) + "/";
        Log(context, Pipelines.Card, null, "Started");

        var files = blobs.Values
            .Where(b => b.Container == LandingContainer && b.Name.StartsWith(folder, StringComparison.Ordinal))
            .Select(b => (Blob: b, FileName: b.Name[folder.Length..]))
            .Where(f => !f.FileName.Contains('/', StringComparison.Ordinal)
                        && f.FileName.StartsWith("settlement_", StringComparison.Ordinal)
                        && f.FileName.EndsWith(".csv", StringComparison.Ordinal))
            .ToList();

        var staged = new List<CardSettlementRow>();
        foreach (var (blob, fileName) in files)
        {
            var raw = SettlementValidator.Land(blob.Content);
            var result = SettlementValidator.Validate(raw, date);
            var metadata = new Dictionary<string, string> { ["retain_until"] = RetainUntil, ["batch_run_id"] = RunToken(context.Spec.Run) };
            if (result.IsValid)
            {
                staged.AddRange(Promote(raw, fileName));
                PutBlob(ArchiveContainer, folder + fileName, blob.Content, metadata);
                blobs.Remove(blob.Path);
                cardFileLog.Add(new CardFileLogRow(context.Spec.Run, date, fileName, "Archived", result.DetailRowCount, result.TrailerRowCount,
                    result.DetailTotalAmount, result.TrailerTotalAmount, null, $"{ArchiveContainer}/{Date(date)}/{fileName}"));
            }
            else
            {
                PutBlob(QuarantineContainer, folder + fileName + ".errors.json", ErrorManifest(date, fileName, result, context.Spec.Run), new Dictionary<string, string>());
                PutBlob(QuarantineContainer, folder + fileName, blob.Content, metadata);
                blobs.Remove(blob.Path);
                cardFileLog.Add(new CardFileLogRow(context.Spec.Run, date, fileName, "Quarantined", result.DetailRowCount, result.TrailerRowCount,
                    null, null, result.ErrorSummary, $"{QuarantineContainer}/{Date(date)}/{fileName}"));
            }
        }

        if (staged.GroupBy(s => s.ProcessorRefNbr, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            return ChildResult.Failed("54100", "The same processor reference number appears in more than one settlement file.");
        }

        var (ins, upd) = Merge(coreCard, staged, c => c.ProcessorRefNbr, (_, _) => true);
        LogMerge(context, Pipelines.Card, "MergeCardSettlement", "core.CardSettlement", staged.Count, ins, upd);
        Log(context, Pipelines.Card, null, "Succeeded", recordCount: files.Count, notes: files.Count == 0
            ? "No settlement files found in the landing folder."
            : string.Create(Invariant, $"{files.Count} settlement file(s) processed; see ops.CardFileLog."));
        return ChildResult.Ok;
    }

    // PL_MapAndAggregate: refresh ref.TxnCodeGlMap from KeyBridge, fail with TXN_CODE_UNMAPPED if a code
    // posted on the business date has no mapping, otherwise replace the date's core.DailyGlTotals.
    private ChildResult MapAndAggregate(RunContext context)
    {
        var date = context.Spec.BusinessDate;
        Log(context, Pipelines.Map, null, "Started");
        context.KeyBridgeRequests++;
        RefreshGlMap();

        var mapped = glMap.Select(m => m.TxnCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unmapped = coreTransactions.Values.Where(t => t.PostingDate == date && !mapped.Contains(t.TxnCode))
            .Select(t => t.TxnCode.TrimEnd(' ')).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (unmapped.Count > 0)
        {
            var message = "Unmapped transaction codes: " + string.Join(',', unmapped);
            Log(context, Pipelines.Map, "CheckTxnCodeMapping", "Failed", recordCount: unmapped.Count, errorCode: "TXN_CODE_UNMAPPED");
            context.Messages.Add(new MessageExpectation(Pipelines.Map, "CheckTxnCodeMapping", message));
            return ChildResult.Failed("TXN_CODE_UNMAPPED", message);
        }

        dailyGlTotals.RemoveAll(d => d.BusinessDate == date);
        dailyGlTotals.AddRange(GlAllocation.DailyTotals(date, coreTransactions.Values, glMap));
        Log(context, Pipelines.Map, null, "Succeeded", objectName: "core.DailyGlTotals");
        return ChildResult.Ok;
    }

    // PL_Reconciliation + ops.usp_RecordReconExceptions / usp_MarkReconAlertSent.
    private ChildResult Reconcile(RunContext context)
    {
        var date = context.Spec.BusinessDate;
        var tolerance = ParseDecimal(context.Spec.Tolerance);
        Log(context, Pipelines.Recon, null, "Started");

        var card = CardTotals(date);
        var gl = GlDetail(date);
        var current = new List<ReconExceptionRow>();
        if (Math.Abs(card.Difference) > tolerance)
        {
            current.Add(new(date, "CardSettlement", "ALL", card.KeyStone, card.Settlement, card.Difference, tolerance,
                "Card settlement file total differs from KeyStone card transactions.", context.Spec.Run, null));
        }

        current.AddRange(gl.Where(g => Math.Abs(g.Difference) > tolerance).Select(g => new ReconExceptionRow(date, "GeneralLedger",
            $"{g.Account.TrimEnd(' ')}:{g.Side}", g.KeyStone, g.Compared, g.Difference, tolerance,
            $"Derived {g.Side.ToLowerInvariant()} total for GL {g.Account.TrimEnd(' ')} differs from KeyStone GL entries.", context.Spec.Run, null)));

        bool Same(ReconExceptionRow a, ReconExceptionRow b) => a.BusinessDate == b.BusinessDate && a.CheckName == b.CheckName && a.ItemKey == b.ItemKey && a.Difference == b.Difference;
        reconExceptions.AddRange(current.Where(c => !reconExceptions.Any(e => Same(e, c))));
        var alertRequired = reconExceptions.Any(e => e.AlertRun is null && current.Any(c => Same(e, c)));

        if (alertRequired)
        {
            var summary = string.Join("; ", current.OrderBy(c => c.CheckName, StringComparer.Ordinal).ThenBy(c => c.ItemKey, StringComparer.Ordinal)
                .Select(c => $"{c.CheckName} {c.ItemKey} difference {Decimal(c.Difference, 2)}"));
            var payload = new JsonObject
            {
                ["type"] = "ReconciliationException",
                ["businessDate"] = Date(date),
                ["batchRunId"] = RunToken(context.Spec.Run),
                ["cardDifference"] = JsonValue.Create(card.Difference),
                ["glItemsOutOfTolerance"] = gl.Count(g => Math.Abs(g.Difference) > tolerance),
                ["exceptionCount"] = current.Count,
                ["summary"] = summary[..Math.Min(1000, summary.Length)],
            };
            context.Alerts.Add(payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
            for (var i = 0; i < reconExceptions.Count; i++)
            {
                if (reconExceptions[i].BusinessDate == date && reconExceptions[i].AlertRun is null)
                {
                    reconExceptions[i] = reconExceptions[i] with { AlertRun = context.Spec.Run };
                }
            }
        }

        Log(context, Pipelines.Recon, null, current.Count > 0 ? "Exceptions" : "Reconciled", recordCount: current.Count,
            notes: $"card difference={Decimal(card.Difference, 2)}; GL items out of tolerance={gl.Count(g => Math.Abs(g.Difference) > tolerance)}; tolerance={context.Spec.Tolerance}");
        return ChildResult.Ok;
    }

    public (decimal Settlement, decimal KeyStone, decimal Difference) CardTotals(DateOnly date)
    {
        var settlement = coreCard.Values.Where(c => c.SettlementDate == date)
            .Sum(c => c.TxnType switch { "SALE" => c.Amount, "RFND" => -c.Amount, _ => 0m });
        var keyStoneTotal = coreTransactions.Values.Where(t => t.PostingDate == date)
            .Sum(t => t.TxnCode switch { "CPOS" => t.Amount, "CRFD" => -t.Amount, _ => 0m });
        return (settlement, keyStoneTotal, settlement - keyStoneTotal);
    }

    public IReadOnlyList<(string Account, string Side, decimal KeyStone, decimal Compared, decimal Difference)> GlDetail(DateOnly date)
    {
        var ks = coreGlEntries.Values.Where(g => g.PostingDate == date).GroupBy(g => g.GlAccountNbr, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (Debit: g.Sum(x => x.DebitAmount), Credit: g.Sum(x => x.CreditAmount)), StringComparer.Ordinal);
        var dg = dailyGlTotals.Where(d => d.BusinessDate == date)
            .ToDictionary(d => d.GlAccountNbr, d => (Debit: d.DebitTotal, Credit: d.CreditTotal), StringComparer.Ordinal);
        return [.. ks.Keys.Union(dg.Keys).Order(StringComparer.Ordinal).SelectMany(account =>
        {
            var k = ks.GetValueOrDefault(account);
            var d = dg.GetValueOrDefault(account);
            return new[]
            {
                (account, "CREDIT", k.Credit, d.Credit, d.Credit - k.Credit),
                (account, "DEBIT", k.Debit, d.Debit, d.Debit - k.Debit),
            };
        })];
    }

    private List<EvidenceEntry> BuildEvidence(RunContext context)
    {
        var date = context.Spec.BusinessDate;
        var e = new List<EvidenceEntry>();
        foreach (var f in cardFileLog.Where(f => f.Run == context.Spec.Run))
        {
            e.Add(new("Files", f.SourceFile, $"{f.Outcome}; detailRows={Count(f.DetailRowCount)}; trailerRows={Count(f.TrailerRowCount)}; errors={f.ErrorSummary ?? "-"}"));
        }

        var txns = coreTransactions.Values.Where(t => t.PostingDate == date).ToList();
        var accounts = coreAccounts.Values.Where(a => a.LastActivityDate == date).ToList();
        var gl = coreGlEntries.Values.Where(g => g.PostingDate == date).ToList();
        var card = coreCard.Values.Where(c => c.SettlementDate == date).ToList();
        var totals = dailyGlTotals.Where(d => d.BusinessDate == date).ToList();
        e.Add(new("RowCounts", "core.PostedTransaction", Integer(txns.Count)));
        e.Add(new("RowCounts", "core.Account", Integer(accounts.Count)));
        e.Add(new("RowCounts", "core.GlEntry", Integer(gl.Count)));
        e.Add(new("RowCounts", "core.CardSettlement", Integer(card.Count)));
        e.Add(new("RowCounts", "core.DailyGlTotals", Integer(totals.Count)));
        e.Add(new("Checksums", "core.PostedTransaction", EvidenceChecksum.PostedTransactions(txns)));
        e.Add(new("Checksums", "core.Account", EvidenceChecksum.Accounts(accounts)));
        e.Add(new("Checksums", "core.GlEntry", EvidenceChecksum.GlEntries(gl)));
        e.Add(new("Checksums", "core.CardSettlement", EvidenceChecksum.CardSettlements(card)));
        e.Add(new("Checksums", "core.DailyGlTotals", EvidenceChecksum.DailyGlTotals(totals)));

        var c = CardTotals(date);
        e.Add(new("Reconciliation", "CardSettlement", $"settlement={Decimal(c.Settlement, 2)}; keystone={Decimal(c.KeyStone, 2)}; difference={Decimal(c.Difference, 2)}"));
        foreach (var g in GlDetail(date))
        {
            e.Add(new("Reconciliation", $"GeneralLedger/{g.Account.TrimEnd(' ')}:{g.Side}",
                $"derived={Decimal(g.Compared, 2)}; keystone={Decimal(g.KeyStone, 2)}; difference={Decimal(g.Difference, 2)}"));
        }

        foreach (var x in reconExceptions.Where(x => x.BusinessDate == date))
        {
            e.Add(new("Exceptions", $"{x.CheckName}/{x.ItemKey}",
                $"difference={Decimal(x.Difference, 2)}; tolerance={Decimal(x.Tolerance, 2)}; detectedUtc={UtcToken}; alertSentUtc={(x.AlertRun is null ? "-" : UtcToken)}"));
        }

        return e;
    }

    private void RefreshGlMap()
    {
        var incoming = keyBridge.Mappings.Select(m => new TxnCodeGlMapRow(Char(m.TransactionCode.Trim(), 4), Char(m.GlAccountNumber.Trim(), 10),
            m.Side == "DEBIT" ? "D" : "C", decimal.Round(m.AllocationRatio, 6), m.Description, keyBridge.Version)).ToList();
        var next = new List<TxnCodeGlMapRow>();
        foreach (var row in incoming)
        {
            var existing = glMap.FirstOrDefault(m => m.TxnCode == row.TxnCode && m.GlAccountNbr == row.GlAccountNbr && m.DrCr == row.DrCr);
            next.Add(existing is not null && existing.AllocationRatio == row.AllocationRatio && existing.Description == row.Description ? existing : row);
        }

        glMap.Clear();
        glMap.AddRange(next.OrderBy(m => m.TxnCode, StringComparer.Ordinal).ThenBy(m => m.GlAccountNbr, StringComparer.Ordinal).ThenBy(m => m.DrCr, StringComparer.Ordinal));
    }

    // stg.usp_PromoteCardSettlementFile: SettlementDate comes from the header, Amount is
    // CONVERT(decimal(15,2), text), empty MerchantName/Mcc become NULL (the dataset's nullValue is "").
    private static IEnumerable<CardSettlementRow> Promote(IReadOnlyList<RawSettlementRecord> raw, string fileName)
    {
        var settlementDate = DateOnly.ParseExact(raw.First(r => r.RecordType?.Trim() == "H").Field(2)!, "yyyyMMdd", Invariant);
        return raw.Where(r => r.RecordType?.Trim() == "D").Select(d => new CardSettlementRow(
            d.Field(1)!.Trim(' '), settlementDate, DateOnly.ParseExact(d.Field(2)!, "yyyyMMdd", Invariant), d.Field(3)!, d.Field(4)!,
            decimal.Round(decimal.Parse(d.Field(5)!, NumberStyles.AllowDecimalPoint, Invariant), 2, MidpointRounding.AwayFromZero),
            Char(d.Field(6)!, 4), d.Field(7), d.Field(8), Char(d.Field(9)!, 3), fileName));
    }

    private static byte[] ErrorManifest(DateOnly date, string fileName, SettlementValidationResult result, int run)
    {
        var array = new JsonArray();
        foreach (var e in result.Errors)
        {
            array.Add(new JsonObject
            {
                ["businessDate"] = Date(date),
                ["sourceFile"] = fileName,
                ["batchRunId"] = RunToken(run),
                ["recordType"] = e.RecordType,
                ["processorRefNbr"] = e.ProcessorRefNbr,
                ["errorCode"] = e.ErrorCode,
                ["errorMessage"] = e.ErrorMessage,
            });
        }

        var json = array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        return Canonical.CanonicalCsv.Encoding.GetBytes(json);
    }

    private void PutBlob(string container, string name, byte[] content, IReadOnlyDictionary<string, string> metadata)
    {
        var blob = new BlobItem(container, name, content, metadata);
        blobs[blob.Path] = blob;
    }

    private static (int Inserted, int Updated) Merge<TKey, TRow>(SortedDictionary<TKey, TRow> target, IEnumerable<TRow> staged, Func<TRow, TKey> key, Func<TRow, TRow, bool> mayUpdate)
        where TKey : notnull
    {
        int inserted = 0, updated = 0;
        foreach (var s in staged)
        {
            if (!target.TryGetValue(key(s), out var t))
            {
                target[key(s)] = s;
                inserted++;
            }
            else if (mayUpdate(s, t) && !EqualityComparer<TRow>.Default.Equals(s, t))
            {
                target[key(s)] = s;
                updated++;
            }
        }

        return (inserted, updated);
    }

    private void LogMerge(RunContext context, string pipeline, string step, string table, int staged, int inserted, int updated) =>
        Log(context, pipeline, step, "Succeeded", objectName: table, recordCount: staged,
            notes: string.Create(Invariant, $"staged={staged}; inserted={inserted}; updated={updated}; unchanged={staged - inserted - updated}"));

    private void Log(RunContext context, string pipeline, string? step, string status, string? objectName = null, long? recordCount = null, string? errorCode = null, string? notes = null) =>
        batchRun.Add(new BatchRunEvent(context.Spec.Run, context.Spec.BusinessDate, pipeline, step, status, objectName, recordCount, errorCode,
            pipeline == Pipelines.Master && status == "Started" ? null : notes));

    private static string Count(int? value) => value is { } v ? Integer(v) : "-";

    private sealed record ChildResult(bool Succeeded, string? ErrorCode, string? Message)
    {
        public static ChildResult Ok { get; } = new(true, null, null);

        public static ChildResult Failed(string errorCode, string message) => new(false, errorCode, message);
    }

    private sealed class RunContext(RunSpec spec)
    {
        public RunSpec Spec { get; } = spec;

        public string Status { get; private set; } = "Succeeded";

        public string? ErrorCode { get; private set; }

        public string? FailedStep { get; private set; }

        public List<string> PipelinesRun { get; } = [];

        public List<MessageExpectation> Messages { get; } = [];

        public List<string> Alerts { get; } = [];

        public int KeyBridgeRequests { get; set; }

        public List<EvidenceEntry>? Evidence { get; set; }

        public void Fail(string? errorCode, string step)
        {
            if (Status == "Failed")
            {
                return;
            }

            Status = "Failed";
            ErrorCode = errorCode;
            FailedStep = step;
        }
    }
}
