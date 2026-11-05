using System.Security.Cryptography;
using System.Text;
using CreditUnion.Batch.TestData.KeyStone;
using CreditUnion.Batch.TestData.Reporting;
using static CreditUnion.Batch.TestData.Canonical.CanonicalValue;

namespace CreditUnion.Batch.TestData.Oracle;

/// <summary>
/// C# statement of the evidence checksums computed by <c>ops.usp_GetRunEvidence</c>: per table, one
/// line per row of the business date in primary-key order, values joined by <c>|</c> as T-SQL
/// <c>CONCAT_WS</c> renders them (CHAR padding kept, decimals at their scale, dates style 23,
/// datetime2 style 121, NULL written as <c>\N</c>), lines joined with LF, hashed as UTF-16LE and shown
/// as uppercase hex. This is a different checksum from the golden-file one (ADR 0006); it exists so
/// AT-14 can check the evidence file, and GoldenConsistencyTests prove it equals the procedure's.
/// </summary>
public static class EvidenceChecksum
{
    private const string Null = "\\N";

    public static string PostedTransactions(IEnumerable<PostedTxn> rows) => Hash(rows.OrderBy(t => t.TxnId).Select(t => Join(
        Integer(t.TxnId), t.AccountNbr, t.TxnCode, Date(t.PostingDate), Date(t.EffectiveDate), Style121(t.PostedTs), Decimal(t.Amount, 2),
        t.Description ?? Null, t.Channel, t.CardProcessorRef ?? Null, t.ReversalOfTxnId is { } r ? Integer(r) : Null)));

    public static string Accounts(IEnumerable<Account> rows) => Hash(rows.OrderBy(a => a.AccountNbr, StringComparer.Ordinal).Select(a => Join(
        a.AccountNbr, a.MemberNbr, a.AccountCategory, a.ProductCode, a.Description ?? Null, Date(a.OpenDate),
        a.CloseDate is { } c ? Date(c) : Null, Decimal(a.CurrentBalance, 2), a.AvailableBalance is { } av ? Decimal(av, 2) : Null,
        Decimal(a.InterestRate, 6), Decimal(a.AccruedInterest, 8), a.Status, Date(a.LastActivityDate), Style121(a.LastMaintTs))));

    public static string GlEntries(IEnumerable<GlEntry> rows) => Hash(rows.OrderBy(g => g.GlEntryId).Select(g => Join(
        Integer(g.GlEntryId), g.GlAccountNbr, Date(g.PostingDate), Decimal(g.DebitAmount, 2), Decimal(g.CreditAmount, 2), g.SourceCode,
        g.BatchRef ?? Null, g.Memo ?? Null, Style121(g.CreatedTs))));

    public static string CardSettlements(IEnumerable<CardSettlementRow> rows) => Hash(rows.OrderBy(c => c.ProcessorRefNbr, StringComparer.OrdinalIgnoreCase).Select(c => Join(
        c.ProcessorRefNbr, Date(c.SettlementDate), Date(c.TxnDate), c.CardBin, c.CardLast4, Decimal(c.Amount, 2), c.TxnType,
        c.MerchantName ?? Null, c.Mcc ?? Null, c.CurrencyCode, c.SourceFile)));

    public static string DailyGlTotals(IEnumerable<DailyGlTotalRow> rows) => Hash(rows.OrderBy(d => d.GlAccountNbr, StringComparer.Ordinal).Select(d => Join(
        Date(d.BusinessDate), d.GlAccountNbr, Decimal(d.DebitTotal, 2), Decimal(d.CreditTotal, 2), Decimal(d.NetAmount, 2), Integer(d.TxnCount))));

    private static string Style121(DateTime value) => Timestamp(value, 6).Replace('T', ' ');

    private static string Join(params string[] values) => string.Join('|', values);

    private static string Hash(IEnumerable<string> lines) =>
        Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(string.Join('\n', lines))));
}
