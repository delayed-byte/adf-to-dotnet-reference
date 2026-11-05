using System.Text;
using CreditUnion.Batch.TestData.Canonical;

namespace CreditUnion.Batch.TestData.Card;

/// <summary>A detail (D) record exactly as the processor writes it (all fields are text).</summary>
public sealed record SettlementDetail(
    string ProcessorRefNbr,
    string TxnDate,
    string CardBin,
    string CardLast4,
    string Amount,
    string TxnType,
    string MerchantName,
    string Mcc,
    string CurrencyCode);

/// <summary>
/// A card processor settlement file (contracts/card-processor/settlement-file-format.md). The header
/// and trailer fields are text so a scenario can write a deliberately wrong value, such as a trailer
/// count that does not match the detail records (AT-06).
/// </summary>
public sealed record SettlementFile(
    DateOnly BusinessDate,
    string FileName,
    string ProcessorId,
    string HeaderDate,
    string FileSequence,
    IReadOnlyList<SettlementDetail> Details,
    string TrailerCount,
    string TrailerTotal)
{
    /// <summary>Blob path inside the landing container: <c>{businessDate}/{fileName}</c>.</summary>
    public string LandingPath => CanonicalValue.Date(BusinessDate) + "/" + FileName;

    /// <summary>UTF-8 text with LF line endings; fields are quoted only when they contain a comma, quote or line break.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        Line(sb, "H", ProcessorId, HeaderDate, FileSequence);
        foreach (var d in Details)
        {
            Line(sb, "D", d.ProcessorRefNbr, d.TxnDate, d.CardBin, d.CardLast4, d.Amount, d.TxnType, d.MerchantName, d.Mcc, d.CurrencyCode);
        }

        Line(sb, "T", TrailerCount, TrailerTotal);
        return sb.ToString();
    }

    public byte[] RenderBytes() => CanonicalCsv.Encoding.GetBytes(Render());

    private static void Line(StringBuilder sb, params string[] fields) =>
        sb.Append(Rfc4180.Record(fields.Select(f => f.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? Rfc4180.Field(f) : f))).Append('\n');
}
