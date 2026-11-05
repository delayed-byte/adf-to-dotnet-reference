using System.Security.Cryptography;
using System.Text;

namespace CreditUnion.Batch.TestData.Canonical;

/// <summary>
/// Order-independent SHA-256 checksum of a table (ADR 0006).
/// <list type="number">
/// <item>Each row becomes a line: canonical values joined by <c>|</c>; NULL is <c>\N</c>; inside a
/// value <c>\</c>, <c>|</c>, LF and CR are escaped as <c>\\</c>, <c>\|</c>, <c>\n</c>, <c>\r</c>.</item>
/// <item>Row lines are sorted ordinally (UTF-16 code units), which removes any dependence on row order.</item>
/// <item>The digest input is the header line (column names joined by <c>|</c>) followed by the sorted
/// row lines, each terminated by LF, encoded as UTF-8. The result is lowercase hex.</item>
/// </list>
/// Duplicate rows are kept, so the checksum is over the multiset of rows.
/// </summary>
public static class TableChecksum
{
    public static string Compute(TableData table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var lines = table.Rows.Select(RowLine).ToList();
        lines.Sort(StringComparer.Ordinal);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, string.Join('|', table.Columns.Select(Escape)));
        foreach (var line in lines)
        {
            Append(hash, line);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static string RowLine(IReadOnlyList<string?> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return string.Join('|', row.Select(v => v is null ? CanonicalCsv.NullToken : Escape(v)));
    }

    private static void Append(IncrementalHash hash, string line) => hash.AppendData(Encoding.UTF8.GetBytes(line + "\n"));

    private static string Escape(string value)
    {
        if (value.AsSpan().IndexOfAny("\\|\n\r") < 0)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            _ = c switch
            {
                '\\' => sb.Append("\\\\"),
                '|' => sb.Append("\\|"),
                '\n' => sb.Append("\\n"),
                '\r' => sb.Append("\\r"),
                _ => sb.Append(c),
            };
        }

        return sb.ToString();
    }
}
