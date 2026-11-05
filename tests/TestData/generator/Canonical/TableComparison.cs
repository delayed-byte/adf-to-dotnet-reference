using System.Globalization;
using System.Text;

namespace CreditUnion.Batch.TestData.Canonical;

/// <summary>
/// Compares an actual table with its golden version (ADR 0006): row counts, order-independent
/// checksums, and a column-level diff. Rows are matched on the key columns when the table has a key,
/// otherwise as a multiset of whole rows. The report lists the first <c>maxReported</c> mismatches
/// (default 20) and the total, so a large drift stays readable.
/// </summary>
public sealed class TableComparison
{
    public const int DefaultMaxReported = 20;

    private TableComparison(string table, int expectedRows, int actualRows, string expectedChecksum, string actualChecksum, IReadOnlyList<string> mismatches, int totalMismatches)
    {
        Table = table;
        ExpectedRowCount = expectedRows;
        ActualRowCount = actualRows;
        ExpectedChecksum = expectedChecksum;
        ActualChecksum = actualChecksum;
        Mismatches = mismatches;
        TotalMismatches = totalMismatches;
    }

    public string Table { get; }

    public int ExpectedRowCount { get; }

    public int ActualRowCount { get; }

    public string ExpectedChecksum { get; }

    public string ActualChecksum { get; }

    /// <summary>The first mismatches, in a stable order (by key, then column).</summary>
    public IReadOnlyList<string> Mismatches { get; }

    public int TotalMismatches { get; }

    public bool IsMatch => TotalMismatches == 0 && string.Equals(ExpectedChecksum, ActualChecksum, StringComparison.Ordinal);

    public static TableComparison Compare(TableData expected, TableData actual, IReadOnlyList<string> keyColumns, int maxReported = DefaultMaxReported)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(keyColumns);

        var expectedChecksum = TableChecksum.Compute(expected);
        var actualChecksum = TableChecksum.Compute(actual);
        var all = new List<string>();

        if (!expected.Columns.SequenceEqual(actual.Columns, StringComparer.Ordinal))
        {
            all.Add($"columns differ: expected [{string.Join(", ", expected.Columns)}], actual [{string.Join(", ", actual.Columns)}]");
        }
        else if (keyColumns.Count > 0)
        {
            CompareKeyed(expected, actual, keyColumns, all);
        }
        else
        {
            CompareMultiset(expected, actual, all);
        }

        if (all.Count == 0 && !string.Equals(expectedChecksum, actualChecksum, StringComparison.Ordinal))
        {
            all.Add("checksums differ although no row difference was found (duplicate rows?)");
        }

        return new TableComparison(expected.Name, expected.RowCount, actual.RowCount, expectedChecksum, actualChecksum, [.. all.Take(maxReported)], all.Count);
    }

    public string Report()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{Table}: rows expected {ExpectedRowCount}, actual {ActualRowCount}; ")
          .Append(CultureInfo.InvariantCulture, $"checksum expected {ExpectedChecksum[..12]}..., actual {ActualChecksum[..12]}...");
        if (IsMatch)
        {
            return sb.Append(" - match").ToString();
        }

        sb.Append(CultureInfo.InvariantCulture, $"\n  {TotalMismatches} mismatch(es)");
        if (TotalMismatches > Mismatches.Count)
        {
            sb.Append(CultureInfo.InvariantCulture, $", first {Mismatches.Count} shown");
        }

        sb.Append(':');
        foreach (var m in Mismatches)
        {
            sb.Append("\n  - ").Append(m);
        }

        return sb.ToString();
    }

    public override string ToString() => Report();

    private static void CompareKeyed(TableData expected, TableData actual, IReadOnlyList<string> keyColumns, List<string> problems)
    {
        var keyIndexes = keyColumns.Select(expected.ColumnIndex).ToArray();
        var expectedByKey = Index(expected, keyIndexes, "expected", problems);
        var actualByKey = Index(actual, keyIndexes, "actual", problems);

        foreach (var key in expectedByKey.Keys.Union(actualByKey.Keys).Order(StringComparer.Ordinal))
        {
            var label = Label(keyColumns, key);
            if (!actualByKey.TryGetValue(key, out var a))
            {
                problems.Add($"missing row {label}");
                continue;
            }

            if (!expectedByKey.TryGetValue(key, out var e))
            {
                problems.Add($"unexpected row {label}");
                continue;
            }

            for (var c = 0; c < expected.Columns.Count; c++)
            {
                if (!string.Equals(e[c], a[c], StringComparison.Ordinal))
                {
                    problems.Add($"{label} {expected.Columns[c]}: expected {Show(e[c])}, actual {Show(a[c])}");
                }
            }
        }
    }

    private static void CompareMultiset(TableData expected, TableData actual, List<string> problems)
    {
        var remaining = actual.Rows.Select(TableChecksum.RowLine).GroupBy(l => l, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var line in expected.Rows.Select(TableChecksum.RowLine))
        {
            if (remaining.TryGetValue(line, out var count) && count > 0)
            {
                remaining[line] = count - 1;
            }
            else
            {
                missing.Add(line);
            }
        }

        var header = string.Join('|', expected.Columns);
        problems.AddRange(missing.Order(StringComparer.Ordinal).Select(l => $"missing row [{header}] = [{l}]"));
        problems.AddRange(remaining.Where(kv => kv.Value > 0).SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value))
            .Order(StringComparer.Ordinal).Select(l => $"unexpected row [{header}] = [{l}]"));
    }

    private static Dictionary<string, IReadOnlyList<string?>> Index(TableData table, int[] keyIndexes, string side, List<string> problems)
    {
        var map = new Dictionary<string, IReadOnlyList<string?>>(StringComparer.Ordinal);
        foreach (var row in table.Rows)
        {
            var key = string.Join('\u001f', keyIndexes.Select(i => row[i] ?? CanonicalCsv.NullToken));
            if (!map.TryAdd(key, row))
            {
                problems.Add($"duplicate key in {side} rows: {key.Replace('\u001f', '|')}");
            }
        }

        return map;
    }

    private static string Label(IReadOnlyList<string> keyColumns, string key)
    {
        var parts = key.Split('\u001f');
        return "[" + string.Join(", ", keyColumns.Select((k, i) => $"{k}={Show(parts[i])}")) + "]";
    }

    private static string Show(string? value) => value is null ? "NULL" : "'" + value.Replace("\n", "\\n", StringComparison.Ordinal) + "'";
}
