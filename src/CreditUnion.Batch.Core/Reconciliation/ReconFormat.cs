using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CreditUnion.Batch.Core.Reconciliation;

/// <summary>
/// The text PL_Reconciliation builds with ADF expressions, as pure functions (ADR 0013): the Notes of "Log Recon Outcome"
/// and the JSON body of "Post Recon Alert". Money is printed with its SQL scale (decimal(19,2): two decimals, ADR 0006
/// semantics 4) in the invariant culture, never through a floating-point type.
/// </summary>
public static class ReconFormat
{
    /// <summary>The largest amount a decimal(19,2) parameter holds.</summary>
    private const decimal MaxAmount = 99_999_999_999_999_999.99m;

    /// <summary>A decimal(19,2) amount: <c>0.01</c>, <c>-12.50</c>, <c>0.00</c>. Valid as a JSON number.</summary>
    public static string Money(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>@concat('card difference=', string(CardDifference), '; GL items out of tolerance=', string(GlItems), '; tolerance=', tolerance)</c>.
    /// </summary>
    public static string Notes(decimal cardDifference, int glItemsOutOfTolerance, decimal tolerance) =>
        string.Create(CultureInfo.InvariantCulture,
            $"card difference={Money(cardDifference)}; GL items out of tolerance={glItemsOutOfTolerance}; tolerance={Money(tolerance)}");

    /// <summary><c>@if(equals(IsOutOfTolerance, true), 'Exceptions', 'Reconciled')</c>.</summary>
    public static string Status(bool isOutOfTolerance) => isOutOfTolerance ? "Exceptions" : "Reconciled";

    /// <summary>
    /// The ADF body expression with its values filled in, in its property order, without whitespace:
    /// <c>{"type":"ReconciliationException","businessDate":"…","batchRunId":"…","cardDifference":…,"glItemsOutOfTolerance":…,"exceptionCount":…,"summary":"…"}</c>.
    /// ADF's <c>@{}</c> interpolation does no JSON escaping, so a quote or a backslash in the summary would break the
    /// document there; here the summary is written as a JSON string, escaped (ADR 0013).
    /// </summary>
    public static string AlertBody(ReconAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteString("type", ReconAlertWebActivity.AlertType);
            json.WriteString("businessDate", alert.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            json.WriteString("batchRunId", alert.BatchRunId.ToString("D", CultureInfo.InvariantCulture));
            json.WritePropertyName("cardDifference");
            json.WriteRawValue(Money(alert.CardDifference));
            json.WriteNumber("glItemsOutOfTolerance", alert.GlItemsOutOfTolerance);
            json.WriteNumber("exceptionCount", alert.ExceptionCount);
            json.WriteString("summary", WellFormed(alert.Summary));
            json.WriteEndObject();
        }

        return new UTF8Encoding(false).GetString(stream.ToArray());
    }

    /// <summary>Whether a tolerance can be passed to a decimal(19,2) parameter exactly: not negative, at most two decimals, in range.</summary>
    public static void ValidateTolerance(decimal tolerance)
    {
        if (tolerance < 0m || tolerance > MaxAmount || decimal.Round(tolerance, 2) != tolerance)
        {
            throw new ReconInputException("The reconciliation tolerance must be an amount of at least 0 with at most two decimals (the procedures take decimal(19,2)).");
        }
    }

    /// <summary>Replaces unpaired UTF-16 surrogates (SQL's LEFT can split a pair), which JSON cannot carry, with U+FFFD.</summary>
    private static string WellFormed(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(text[i]).Append(text[i + 1]);
                i++;
            }
            else
            {
                builder.Append(char.IsSurrogate(text[i]) ? '�' : text[i]);
            }
        }

        return builder.ToString();
    }
}
