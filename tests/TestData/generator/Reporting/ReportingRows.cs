namespace CreditUnion.Batch.TestData.Reporting;

// Rows of the reporting store as the oracle models them (db/reporting). CHAR values are padded.

public sealed record CardSettlementRow(
    string ProcessorRefNbr,
    DateOnly SettlementDate,
    DateOnly TxnDate,
    string CardBin,
    string CardLast4,
    decimal Amount,
    string TxnType,
    string? MerchantName,
    string? Mcc,
    string CurrencyCode,
    string SourceFile);

public sealed record TxnCodeGlMapRow(string TxnCode, string GlAccountNbr, string DrCr, decimal AllocationRatio, string? Description, string? SourceVersion);

public sealed record DailyGlTotalRow(DateOnly BusinessDate, string GlAccountNbr, decimal DebitTotal, decimal CreditTotal, decimal NetAmount, int TxnCount);

public sealed record ReconExceptionRow(
    DateOnly BusinessDate,
    string CheckName,
    string ItemKey,
    decimal KeyStoneAmount,
    decimal ComparedAmount,
    decimal Difference,
    decimal Tolerance,
    string Description,
    int DetectedRun,
    int? AlertRun);

public sealed record CardFileLogRow(
    int Run,
    DateOnly BusinessDate,
    string SourceFile,
    string Outcome,
    int? DetailRowCount,
    int? TrailerRowCount,
    decimal? DetailTotalAmount,
    decimal? TrailerTotalAmount,
    string? ErrorSummary,
    string? DestinationPath);

public sealed record BatchRunEvent(
    int Run,
    DateOnly BusinessDate,
    string PipelineName,
    string? StepName,
    string Status,
    string? ObjectName = null,
    long? RecordCount = null,
    string? ErrorCode = null,
    string? Notes = null);

/// <summary>A blob in the oracle's model of storage. Content is exact bytes.</summary>
public sealed record BlobItem(string Container, string Name, byte[] Content, IReadOnlyDictionary<string, string> Metadata)
{
    public string Path => Container + "/" + Name;
}
