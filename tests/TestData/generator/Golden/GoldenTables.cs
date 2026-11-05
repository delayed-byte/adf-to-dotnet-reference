using CreditUnion.Batch.TestData.Canonical;

namespace CreditUnion.Batch.TestData.Golden;

/// <summary>ADF pipeline names, as logged in ops.BatchRun.</summary>
public static class Pipelines
{
    public const string Master = "PL_Master_NightlyBatch";
    public const string Extract = "PL_ExtractKeyStone";
    public const string Card = "PL_IngestCardSettlement";
    public const string Map = "PL_MapAndAggregate";
    public const string Recon = "PL_Reconciliation";

    public static IReadOnlyList<string> Children { get; } = [Extract, Card, Map, Recon];
}

/// <summary>
/// A reporting-store table compared against golden output. <see cref="SnapshotSql"/> selects the
/// business columns only (load-audit columns such as LoadedBatchRunId and LoadedUtc are excluded, see
/// ADR 0003) and is the single definition shared by the generator (column list) and the harness
/// (database snapshot). The token <c>{runs}</c> stands for a <c>(VALUES (@run1, 1), ...)</c> table of
/// the scenario's batch run IDs and their ordinals, so run-specific GUIDs never appear in golden files.
/// </summary>
public sealed record GoldenTableDefinition(
    string Name,
    IReadOnlyList<ColumnDefinition> Columns,
    IReadOnlyList<string> KeyColumns,
    string? OwnerPipeline,
    string SnapshotSql)
{
    public string FileName => Name + ".csv";

    public IReadOnlyList<string> ColumnNames => [.. Columns.Select(c => c.Name)];
}

public static class GoldenTables
{
    private static readonly ColumnType Money = ColumnType.Decimal(15, 2);
    private static readonly ColumnType Total = ColumnType.Decimal(19, 2);

    public static GoldenTableDefinition PostedTransaction { get; } = new("core.PostedTransaction",
    [
        new("TxnId", ColumnType.BigInt), new("AccountNbr", ColumnType.Char(12)), new("TxnCode", ColumnType.Char(4)),
        new("PostingDate", ColumnType.Date), new("EffectiveDate", ColumnType.Date), new("PostedTs", ColumnType.DateTime2(6)),
        new("Amount", Money), new("Description", ColumnType.VarChar(60), true), new("Channel", ColumnType.Char(3)),
        new("CardProcessorRef", ColumnType.VarChar(24), true), new("ReversalOfTxnId", ColumnType.BigInt, true),
    ], ["TxnId"], Pipelines.Extract,
    "SELECT TxnId, AccountNbr, TxnCode, PostingDate, EffectiveDate, PostedTs, Amount, Description, Channel, CardProcessorRef, ReversalOfTxnId FROM core.PostedTransaction");

    public static GoldenTableDefinition Account { get; } = new("core.Account",
    [
        new("AccountNbr", ColumnType.Char(12)), new("MemberNbr", ColumnType.Char(10)), new("AccountCategory", ColumnType.Char(1)),
        new("ProductCode", ColumnType.Char(6)), new("Description", ColumnType.VarChar(60), true), new("OpenDate", ColumnType.Date),
        new("CloseDate", ColumnType.Date, true), new("CurrentBalance", Money), new("AvailableBalance", Money, true),
        new("InterestRate", ColumnType.Decimal(9, 6)), new("AccruedInterest", ColumnType.Decimal(31, 8)), new("Status", ColumnType.Char(1)),
        new("LastActivityDate", ColumnType.Date), new("LastMaintTs", ColumnType.DateTime2(6)),
    ], ["AccountNbr"], Pipelines.Extract,
    "SELECT AccountNbr, MemberNbr, AccountCategory, ProductCode, Description, OpenDate, CloseDate, CurrentBalance, AvailableBalance, InterestRate, AccruedInterest, Status, LastActivityDate, LastMaintTs FROM core.Account");

    public static GoldenTableDefinition GlEntry { get; } = new("core.GlEntry",
    [
        new("GlEntryId", ColumnType.BigInt), new("GlAccountNbr", ColumnType.Char(10)), new("PostingDate", ColumnType.Date),
        new("DebitAmount", Money), new("CreditAmount", Money), new("SourceCode", ColumnType.Char(4)),
        new("BatchRef", ColumnType.VarChar(20), true), new("Memo", ColumnType.VarChar(80), true), new("CreatedTs", ColumnType.DateTime2(6)),
    ], ["GlEntryId"], Pipelines.Extract,
    "SELECT GlEntryId, GlAccountNbr, PostingDate, DebitAmount, CreditAmount, SourceCode, BatchRef, Memo, CreatedTs FROM core.GlEntry");

    public static GoldenTableDefinition CardSettlement { get; } = new("core.CardSettlement",
    [
        new("ProcessorRefNbr", ColumnType.VarChar(24)), new("SettlementDate", ColumnType.Date), new("TxnDate", ColumnType.Date),
        new("CardBin", ColumnType.Char(6)), new("CardLast4", ColumnType.Char(4)), new("Amount", Money), new("TxnType", ColumnType.Char(4)),
        new("MerchantName", ColumnType.NVarChar(60), true), new("Mcc", ColumnType.Char(4), true), new("CurrencyCode", ColumnType.Char(3)),
        new("SourceFile", ColumnType.NVarChar(400)),
    ], ["ProcessorRefNbr"], Pipelines.Card,
    "SELECT ProcessorRefNbr, SettlementDate, TxnDate, CardBin, CardLast4, Amount, TxnType, MerchantName, Mcc, CurrencyCode, SourceFile FROM core.CardSettlement");

    public static GoldenTableDefinition CardFileLog { get; } = new("ops.CardFileLog",
    [
        new("Run", ColumnType.Int), new("BusinessDate", ColumnType.Date), new("SourceFile", ColumnType.NVarChar(400)),
        new("Outcome", ColumnType.VarChar(20)), new("DetailRowCount", ColumnType.Int, true), new("TrailerRowCount", ColumnType.Int, true),
        new("DetailTotalAmount", Total, true), new("TrailerTotalAmount", Total, true), new("ErrorSummary", ColumnType.NVarChar(2000), true),
        new("DestinationPath", ColumnType.NVarChar(1000), true),
    ], ["Run", "BusinessDate", "SourceFile"], Pipelines.Card,
    "SELECT r.RunOrdinal AS [Run], f.BusinessDate, f.SourceFile, f.Outcome, f.DetailRowCount, f.TrailerRowCount, f.DetailTotalAmount, f.TrailerTotalAmount, f.ErrorSummary, f.DestinationPath " +
    "FROM ops.CardFileLog AS f JOIN {runs} AS r (BatchRunId, RunOrdinal) ON r.BatchRunId = f.BatchRunId");

    public static GoldenTableDefinition TxnCodeGlMap { get; } = new("ref.TxnCodeGlMap",
    [
        new("TxnCode", ColumnType.Char(4)), new("GlAccountNbr", ColumnType.Char(10)), new("DrCr", ColumnType.Char(1)),
        new("AllocationRatio", ColumnType.Decimal(9, 6)), new("Description", ColumnType.NVarChar(100), true),
        new("SourceVersion", ColumnType.VarChar(50), true),
    ], ["TxnCode", "GlAccountNbr", "DrCr"], Pipelines.Map,
    "SELECT TxnCode, GlAccountNbr, DrCr, AllocationRatio, Description, SourceVersion FROM ref.TxnCodeGlMap");

    public static GoldenTableDefinition DailyGlTotals { get; } = new("core.DailyGlTotals",
    [
        new("BusinessDate", ColumnType.Date), new("GlAccountNbr", ColumnType.Char(10)), new("DebitTotal", Total),
        new("CreditTotal", Total), new("NetAmount", Total), new("TxnCount", ColumnType.Int),
    ], ["BusinessDate", "GlAccountNbr"], Pipelines.Map,
    "SELECT BusinessDate, GlAccountNbr, DebitTotal, CreditTotal, NetAmount, TxnCount FROM core.DailyGlTotals");

    public static GoldenTableDefinition ReconExceptions { get; } = new("ops.ReconExceptions",
    [
        new("BusinessDate", ColumnType.Date), new("CheckName", ColumnType.VarChar(30)), new("ItemKey", ColumnType.VarChar(50)),
        new("KeyStoneAmount", Total), new("ComparedAmount", Total), new("Difference", Total), new("Tolerance", Total),
        new("Description", ColumnType.NVarChar(400), true), new("DetectedRun", ColumnType.Int, true), new("AlertRun", ColumnType.Int, true),
    ], ["BusinessDate", "CheckName", "ItemKey", "Difference"], Pipelines.Recon,
    "SELECT e.BusinessDate, e.CheckName, e.ItemKey, e.KeyStoneAmount, e.ComparedAmount, e.Difference, e.Tolerance, e.Description, d.RunOrdinal AS DetectedRun, a.RunOrdinal AS AlertRun " +
    "FROM ops.ReconExceptions AS e LEFT JOIN {runs} AS d (BatchRunId, RunOrdinal) ON d.BatchRunId = e.DetectedBatchRunId " +
    "LEFT JOIN {runs} AS a (BatchRunId, RunOrdinal) ON a.BatchRunId = e.AlertBatchRunId");

    /// <summary>
    /// The audit trail of the scenario's runs. Free text that carries run-specific values is
    /// normalised: the batch run ID in ObjectName becomes <c>{batchRunId}</c>, and the master's Started
    /// note (trigger name and time) is dropped. ErrorMessage is not compared here; scenarios assert
    /// the messages that matter in their manifest (ADR 0006).
    /// </summary>
    public static GoldenTableDefinition BatchRun { get; } = new("ops.BatchRun",
    [
        new("Run", ColumnType.Int), new("BusinessDate", ColumnType.Date), new("PipelineName", ColumnType.VarChar(100)),
        new("StepName", ColumnType.VarChar(100), true), new("Status", ColumnType.VarChar(30)), new("ObjectName", ColumnType.NVarChar(4000), true),
        new("RecordCount", ColumnType.BigInt, true), new("ErrorCode", ColumnType.VarChar(50), true), new("Notes", ColumnType.NVarChar(2000), true),
    ], [], null,
    "SELECT r.RunOrdinal AS [Run], b.BusinessDate, b.PipelineName, b.StepName, b.Status, " +
    "REPLACE(b.ObjectName, CONVERT(nvarchar(36), b.BatchRunId), N'{batchRunId}') AS ObjectName, b.RecordCount, b.ErrorCode, " +
    "CASE WHEN b.PipelineName = 'PL_Master_NightlyBatch' AND b.Status = 'Started' THEN NULL ELSE b.Notes END AS Notes " +
    "FROM ops.BatchRun AS b JOIN {runs} AS r (BatchRunId, RunOrdinal) ON r.BatchRunId = b.BatchRunId");

    public static IReadOnlyList<GoldenTableDefinition> All { get; } =
        [PostedTransaction, Account, GlEntry, CardSettlement, CardFileLog, TxnCodeGlMap, DailyGlTotals, ReconExceptions, BatchRun];

    public static GoldenTableDefinition Get(string name) =>
        All.SingleOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal))
        ?? throw new ArgumentException($"Unknown golden table '{name}'.", nameof(name));

    /// <summary>Tables a pipeline writes; ops.BatchRun is filtered by PipelineName instead.</summary>
    public static IReadOnlyList<GoldenTableDefinition> OwnedBy(string pipeline) =>
        [.. All.Where(t => string.Equals(t.OwnerPipeline, pipeline, StringComparison.Ordinal))];
}
