using System.Text;

namespace CreditUnion.Batch.TestData.Canonical;

/// <summary>One parsed CSV field: its text and whether it was quoted in the source.</summary>
public readonly record struct CsvField(string Text, bool Quoted);

/// <summary>
/// Minimal RFC 4180 reader and writer: comma separator, double-quote quoting with "" as the escape,
/// LF record terminator. A bare CR outside quotes is rejected so that CRLF conversions by git or an
/// editor are caught instead of silently changing values (see .gitattributes).
/// </summary>
public static class Rfc4180
{
    public static IReadOnlyList<IReadOnlyList<CsvField>> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var records = new List<IReadOnlyList<CsvField>>();
        var fields = new List<CsvField>();
        var field = new StringBuilder();
        var quoted = false;
        var inQuotes = false;
        var line = 1;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }

                    inQuotes = false;
                    i++;
                    continue;
                }

                if (c == '\n')
                {
                    line++;
                }

                field.Append(c);
                i++;
                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0 && !quoted:
                    quoted = true;
                    inQuotes = true;
                    break;
                case '"':
                    throw new FormatException($"Line {line}: unexpected quote inside an unquoted field.");
                case ',':
                    fields.Add(new CsvField(field.ToString(), quoted));
                    field.Clear();
                    quoted = false;
                    break;
                case '\r':
                    throw new FormatException($"Line {line}: CR found outside quotes; files must use LF line endings.");
                case '\n':
                    fields.Add(new CsvField(field.ToString(), quoted));
                    records.Add(fields);
                    fields = [];
                    field.Clear();
                    quoted = false;
                    line++;
                    break;
                default:
                    if (quoted)
                    {
                        throw new FormatException($"Line {line}: characters after a closing quote.");
                    }

                    field.Append(c);
                    break;
            }

            i++;
        }

        if (inQuotes)
        {
            throw new FormatException("Unterminated quoted field at end of file.");
        }

        if (field.Length > 0 || quoted || fields.Count > 0)
        {
            fields.Add(new CsvField(field.ToString(), quoted));
            records.Add(fields);
        }

        return records;
    }

    /// <summary>Quotes a field when it is empty or contains a delimiter, quote, line break or edge whitespace.</summary>
    public static string Field(string value, bool alwaysQuoteEmpty = true)
    {
        ArgumentNullException.ThrowIfNull(value);
        var needsQuotes = (alwaysQuoteEmpty && value.Length == 0)
            || value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            || (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])));
        return needsQuotes ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    public static string Record(IEnumerable<string> fields) => string.Join(',', fields);
}
