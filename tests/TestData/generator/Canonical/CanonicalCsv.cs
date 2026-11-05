using System.Text;

namespace CreditUnion.Batch.TestData.Canonical;

/// <summary>
/// The golden/input CSV format (ADR 0006): UTF-8 without BOM, LF line endings, a header row of
/// column names, RFC 4180 quoting. SQL NULL is the unquoted token <c>\N</c>; the empty string is
/// <c>""</c>; a field is quoted when it is empty, equals <c>\N</c>, contains a comma, quote or line
/// break, or starts or ends with whitespace (so CHAR padding and trailing spaces stay visible).
/// </summary>
public static class CanonicalCsv
{
    public const string NullToken = "\\N";

    public static UTF8Encoding Encoding { get; } = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Write(TableData table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var sb = new StringBuilder();
        sb.Append(Rfc4180.Record(table.Columns.Select(c => Rfc4180.Field(c)))).Append('\n');
        foreach (var row in table.Rows)
        {
            sb.Append(Rfc4180.Record(row.Select(FormatField))).Append('\n');
        }

        return sb.ToString();
    }

    public static TableData Read(string name, string text)
    {
        var records = Rfc4180.Parse(text);
        if (records.Count == 0)
        {
            throw new FormatException($"{name}: file is empty; a header row is required.");
        }

        var columns = records[0].Select(f => f.Text).ToArray();
        var rows = records.Skip(1).Select((r, i) => r.Count == columns.Length
            ? r.Select(ParseField).ToArray()
            : throw new FormatException($"{name}: data row {i + 1} has {r.Count} fields; expected {columns.Length}."));
        return new TableData(name, columns, rows);
    }

    public static TableData ReadFile(string path, string? name = null) =>
        Read(name ?? Path.GetFileNameWithoutExtension(path), File.ReadAllText(path, Encoding));

    private static string FormatField(string? value) => value switch
    {
        null => NullToken,
        NullToken => "\"" + NullToken + "\"",
        _ => Rfc4180.Field(value),
    };

    private static string? ParseField(CsvField field) =>
        !field.Quoted && field.Text == NullToken ? null : field.Text;
}
