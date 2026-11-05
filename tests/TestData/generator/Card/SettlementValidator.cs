using System.Globalization;
using CreditUnion.Batch.TestData.Canonical;

namespace CreditUnion.Batch.TestData.Card;

/// <summary>One record as the Copy activity lands it in stg.CardSettlementRaw (empty field = NULL).</summary>
public sealed record RawSettlementRecord(string? RecordType, IReadOnlyList<string?> Fields)
{
    public string? Field(int number) => number <= Fields.Count ? Fields[number - 1] : null;
}

public sealed record SettlementError(string? RecordType, string? ProcessorRefNbr, string ErrorCode, string ErrorMessage);

public sealed record SettlementValidationResult(
    bool IsValid,
    int DetailRowCount,
    int? TrailerRowCount,
    decimal? DetailTotalAmount,
    decimal? TrailerTotalAmount,
    IReadOnlyList<SettlementError> Errors,
    string? ErrorSummary);

/// <summary>
/// C# statement of the settlement file rules, written from the contract and kept in step with
/// <c>stg.usp_ValidateCardSettlementFile</c> (the source of truth). The oracle uses it to derive the
/// expected file outcomes and error manifests; GoldenConsistencyTests run both on the golden files
/// and require identical results. Comparisons follow the database's case-insensitive collation and
/// <c>LEN()</c>, which ignores trailing spaces.
/// </summary>
public static class SettlementValidator
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Parses file bytes the way the ADF DelimitedText dataset does (nullValue "" → NULL).</summary>
    public static IReadOnlyList<RawSettlementRecord> Land(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var text = CanonicalCsv.Encoding.GetString(content);
        return [.. Rfc4180.Parse(text).Select(r =>
        {
            var values = r.Take(10).Select(f => f.Text.Length == 0 ? null : f.Text).ToList();
            while (values.Count < 10)
            {
                values.Add(null);
            }

            return new RawSettlementRecord(values[0], values.Skip(1).ToList());
        })];
    }

    public static SettlementValidationResult Validate(IReadOnlyList<RawSettlementRecord> raw, DateOnly businessDate)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var rows = raw.Select(r => r with { RecordType = r.RecordType?.Trim(' ') }).ToList();
        var errors = new List<SettlementError>();
        var headers = rows.Where(r => Is(r.RecordType, "H")).ToList();
        var trailers = rows.Where(r => Is(r.RecordType, "T")).ToList();
        var details = rows.Where(r => Is(r.RecordType, "D")).ToList();

        foreach (var r in rows.Where(r => !(Is(r.RecordType, "H") || Is(r.RecordType, "D") || Is(r.RecordType, "T"))))
        {
            errors.Add(new(r.RecordType, null, "RECORD_TYPE_INVALID", $"Unknown record type '{r.RecordType ?? "<empty>"}'; expected H, D or T."));
        }

        ValidateHeader(headers, businessDate, errors);

        foreach (var d in details)
        {
            var bad = BadFields(d);
            if (bad.Count > 0)
            {
                errors.Add(new("D", d.Field(1), "ROW_SCHEMA_INVALID", "Invalid field(s): " + string.Join(", ", bad)));
            }
        }

        foreach (var group in details.Where(d => !IsBlank(d.Field(1)))
                     .GroupBy(d => d.Field(1)!.TrimEnd(' '), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            errors.Add(new("D", group.First().Field(1), "DUPLICATE_PROCESSOR_REF", $"Processor reference appears {group.Count()} times in the file."));
        }

        var amounts = details.Select(d => TryDecimal(d.Field(5), 15)).Where(a => a.HasValue).Select(a => a!.Value).ToList();
        decimal? detailTotal = amounts.Count == 0 ? null : amounts.Sum();
        int? trailerRowCount = null;
        decimal? trailerTotal = null;

        if (trailers.Count == 0)
        {
            errors.Add(new("T", null, "TRAILER_MISSING", "File has no trailer (T) record."));
        }
        else if (trailers.Count > 1)
        {
            errors.Add(new("T", null, "TRAILER_DUPLICATE", $"File has {trailers.Count} trailer (T) records; expected 1."));
        }
        else
        {
            var countText = trailers[0].Field(1);
            var totalText = trailers[0].Field(2);
            trailerRowCount = countText is not null && countText.All(char.IsAsciiDigit) ? TryInt(countText) : null;
            trailerTotal = totalText is not null && totalText.All(c => char.IsAsciiDigit(c) || c == '.') ? TryDecimal(totalText, 19) : null;

            if (trailerRowCount is null)
            {
                errors.Add(new("T", null, "TRAILER_INVALID", "Trailer record count is not a whole number."));
            }
            else if (trailerRowCount != details.Count)
            {
                errors.Add(new("T", null, "TRAILER_COUNT_MISMATCH", $"Trailer record count {trailerRowCount} does not match {details.Count} detail records."));
            }

            if (trailerTotal is null)
            {
                errors.Add(new("T", null, "TRAILER_INVALID", "Trailer amount total is not a valid amount."));
            }
            else if (!errors.Any(e => e.ErrorCode == "ROW_SCHEMA_INVALID") && trailerTotal != (detailTotal ?? 0m))
            {
                errors.Add(new("T", null, "TRAILER_TOTAL_MISMATCH",
                    $"Trailer amount total {Money(trailerTotal.Value)} does not match detail total {Money(detailTotal ?? 0m)}."));
            }
        }

        var ordered = errors
            .OrderBy(e => e.RecordType?.ToUpperInvariant() switch { "H" => 0, "D" => 1, "T" => 2, _ => 3 })
            .ThenBy(e => e.ProcessorRefNbr is null ? 0 : 1)
            .ThenBy(e => e.ProcessorRefNbr, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.ErrorCode, StringComparer.Ordinal)
            .ToList();
        var summary = errors.Count == 0 ? null : string.Join("; ", errors.GroupBy(e => e.ErrorCode).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => string.Create(Invariant, $"{g.Key} x{g.Count()}")));

        return new SettlementValidationResult(errors.Count == 0, details.Count, trailerRowCount,
            detailTotal is null ? null : decimal.Round(detailTotal.Value, 2), trailerTotal, ordered, summary is null ? null : summary[..Math.Min(2000, summary.Length)]);
    }

    private static void ValidateHeader(List<RawSettlementRecord> headers, DateOnly businessDate, List<SettlementError> errors)
    {
        if (headers.Count == 0)
        {
            errors.Add(new("H", null, "HEADER_MISSING", "File has no header (H) record."));
            return;
        }

        if (headers.Count > 1)
        {
            errors.Add(new("H", null, "HEADER_DUPLICATE", $"File has {headers.Count} header (H) records; expected 1."));
            return;
        }

        if (IsBlank(headers[0].Field(1)))
        {
            errors.Add(new("H", null, "HEADER_INVALID", "Header processor id is empty."));
        }

        var dateText = headers[0].Field(2);
        var date = ParseYyyyMmDd(dateText);
        if (Len(dateText) != 8 || date is null)
        {
            errors.Add(new("H", null, "HEADER_INVALID", "Header settlement date is not a valid yyyyMMdd date."));
        }
        else if (date != businessDate)
        {
            errors.Add(new("H", null, "HEADER_DATE_MISMATCH",
                $"Header settlement date {dateText} does not match business date {businessDate.ToString("yyyyMMdd", Invariant)}."));
        }
    }

    private static List<string> BadFields(RawSettlementRecord d)
    {
        var bad = new List<string>();
        if (IsBlank(d.Field(1)) || Len(d.Field(1)) > 24)
        {
            bad.Add("ProcessorRefNbr");
        }

        if (Len(d.Field(2)) != 8 || ParseYyyyMmDd(d.Field(2)) is null)
        {
            bad.Add("TxnDate");
        }

        if (!Digits(d.Field(3), 6))
        {
            bad.Add("CardBin");
        }

        if (!Digits(d.Field(4), 4))
        {
            bad.Add("CardLast4");
        }

        if (!ValidAmount(d.Field(5)))
        {
            bad.Add("Amount");
        }

        if (!(Is(d.Field(6), "SALE") || Is(d.Field(6), "RFND")))
        {
            bad.Add("TxnType");
        }

        if (Len(d.Field(7)) > 60)
        {
            bad.Add("MerchantName");
        }

        if (!string.IsNullOrEmpty(d.Field(8)) && !Digits(d.Field(8), 4))
        {
            bad.Add("Mcc");
        }

        if (!Is(d.Field(9), "USD"))
        {
            bad.Add("CurrencyCode");
        }

        return bad;
    }

    private static bool ValidAmount(string? text)
    {
        if (text is null || text.Any(c => !(char.IsAsciiDigit(c) || c == '.')) || !text.Any(char.IsAsciiDigit) || text.Count(c => c == '.') > 1)
        {
            return false;
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0 && Len(text) - (dot + 1) is < 1 or > 2)
        {
            return false;
        }

        return TryDecimal(text, 15) is > 0m;
    }

    private static decimal? TryDecimal(string? text, int precision)
    {
        const NumberStyles TryConvertStyles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
        if (text is null || !decimal.TryParse(text, TryConvertStyles, Invariant, out var value))
        {
            return null;
        }

        value = decimal.Round(value, 2, MidpointRounding.AwayFromZero);
        return decimal.Truncate(decimal.Abs(value)).ToString(Invariant).Length > precision - 2 ? null : value;
    }

    private static int? TryInt(string text) =>
        text.Length == 0 ? 0 : int.TryParse(text, NumberStyles.None, Invariant, out var value) ? value : null;

    private static DateOnly? ParseYyyyMmDd(string? text) =>
        text is not null && DateOnly.TryParseExact(text, "yyyyMMdd", Invariant, DateTimeStyles.None, out var d) ? d : null;

    private static bool Digits(string? text, int count) => text is not null && text.Length == count && text.All(char.IsAsciiDigit);

    private static bool Is(string? value, string expected) => string.Equals(value?.TrimEnd(' '), expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value?.Trim(' '));

    private static int Len(string? value) => value?.TrimEnd(' ').Length ?? 0;

    private static string Money(decimal value) => CanonicalValue.Decimal(decimal.Round(value, 2), 2);
}
