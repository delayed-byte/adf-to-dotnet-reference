using System.Runtime.CompilerServices;
using System.Text;

namespace CreditUnion.Batch.Core.Card;

/// <summary>
/// Reads a settlement file the way the ADF dataset DS_Blob_CardSettlementCsv describes it (ADR 0010):
/// UTF-8, comma delimiter, <c>"</c> as both quote and escape, no header row, row delimiter LF, an empty
/// value is null (<c>nullValue ""</c>). Every value stays a string. It streams: only the current record is held.
/// </summary>
public static class SettlementCsvReader
{
    /// <summary>The ordinals the copy maps: 1 is RecordType and 2 to 10 are Field01 to Field09.</summary>
    public const int ColumnCount = 10;

    /// <summary>Width of the staging columns (<c>nvarchar(200)</c>).</summary>
    public const int MaxFieldLength = 200;

    /// <summary>
    /// A reader for UTF-8 text. A leading byte-order mark is skipped, and invalid bytes become U+FFFD, as a
    /// lenient UTF-8 decoder does.
    /// </summary>
    public static StreamReader OpenText(Stream stream) =>
        new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: false), detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024);

    /// <summary>
    /// The records of the file, each padded or cut to <see cref="ColumnCount"/> values. A record with fewer fields
    /// gets null for the rest (a trailer has three, a header four); fields beyond the tenth are not mapped and are dropped.
    /// Empty lines are skipped. A quoted field may contain commas, doubled quotes and line feeds. A CR is data, so a
    /// CRLF file's last field ends in CR and fails validation in SQL, as it would in ADF with <c>rowDelimiter "\n"</c>.
    /// </summary>
    public static async IAsyncEnumerable<string?[]> ReadAsync(TextReader reader, string fileName, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var records = 0L;
        var fields = new List<string?>(ColumnCount);
        var field = new StringBuilder();
        var buffer = new char[64 * 1024];
        var state = State.FieldStart;
        var lineHasContent = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (state == State.InQuotes)
                {
                    if (c == '"')
                    {
                        state = State.QuoteInQuotes;
                    }
                    else
                    {
                        Append(field, c, fileName, records + 1, fields.Count + 1);
                    }

                    continue;
                }

                if (state == State.QuoteInQuotes)
                {
                    if (c == '"')
                    {
                        // escapeChar and quoteChar are both ": a doubled quote is one literal quote.
                        Append(field, '"', fileName, records + 1, fields.Count + 1);
                        state = State.InQuotes;
                        continue;
                    }

                    state = State.AfterQuote; // the quote closed the field; c is handled below
                }

                if (c == '\n')
                {
                    if (lineHasContent)
                    {
                        EndField(fields, field);
                        records++;
                        yield return Shape(fields);
                    }

                    fields.Clear();
                    lineHasContent = false;
                    state = State.FieldStart;
                }
                else if (c == ',')
                {
                    EndField(fields, field);
                    state = State.FieldStart;
                    lineHasContent = true;
                }
                else if (c == '"' && state == State.FieldStart)
                {
                    state = State.InQuotes;
                    lineHasContent = true;
                }
                else
                {
                    // A quote inside an unquoted field, or text after a closing quote, is data.
                    Append(field, c, fileName, records + 1, fields.Count + 1);
                    state = state == State.AfterQuote ? State.AfterQuote : State.Unquoted;
                    lineHasContent = true;
                }
            }
        }

        if (state == State.InQuotes)
        {
            throw new CardFileDataException($"{fileName} record {records + 1}: a quoted field is not closed before the end of the file.");
        }

        if (lineHasContent)
        {
            EndField(fields, field);
            yield return Shape(fields);
        }
    }

    private static void Append(StringBuilder field, char c, string fileName, long record, int column)
    {
        if (field.Length >= MaxFieldLength)
        {
            throw new CardFileDataException(
                $"{fileName} record {record} field {column}: the value is longer than the {MaxFieldLength}-character staging column; the value is not logged.");
        }

        field.Append(c);
    }

    private static void EndField(List<string?> fields, StringBuilder field)
    {
        // nullValue "" reads an empty value as null, quoted or not (ADR 0010).
        fields.Add(field.Length == 0 ? null : field.ToString());
        field.Clear();
    }

    private static string?[] Shape(List<string?> fields)
    {
        var shaped = new string?[ColumnCount];
        for (var i = 0; i < ColumnCount && i < fields.Count; i++)
        {
            shaped[i] = fields[i];
        }

        return shaped;
    }

    private enum State
    {
        FieldStart,
        Unquoted,
        InQuotes,
        QuoteInQuotes,
        AfterQuote,
    }
}
