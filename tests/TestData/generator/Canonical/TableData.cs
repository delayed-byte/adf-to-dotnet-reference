namespace CreditUnion.Batch.TestData.Canonical;

/// <summary>
/// A table of canonical text values (null = SQL NULL). This is the common currency of the harness:
/// golden CSV files, input files and database snapshots are all read into it, and the checksum and
/// comparison work on it alone.
/// </summary>
public sealed class TableData
{
    public TableData(string name, IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<string?>> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        Name = name;
        Columns = [.. columns];
        var list = new List<IReadOnlyList<string?>>();
        foreach (var row in rows)
        {
            if (row.Count != Columns.Count)
            {
                throw new ArgumentException($"{name}: row {list.Count + 1} has {row.Count} values; expected {Columns.Count}.", nameof(rows));
            }

            list.Add([.. row]);
        }

        Rows = list;
    }

    public string Name { get; }

    public IReadOnlyList<string> Columns { get; }

    public IReadOnlyList<IReadOnlyList<string?>> Rows { get; }

    public int RowCount => Rows.Count;

    public int ColumnIndex(string column)
    {
        for (var i = 0; i < Columns.Count; i++)
        {
            if (string.Equals(Columns[i], column, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new ArgumentException($"{Name} has no column '{column}'. Columns: {string.Join(", ", Columns)}.", nameof(column));
    }

    public string? Value(int row, string column) => Rows[row][ColumnIndex(column)];

    /// <summary>Rows whose <paramref name="column"/> is one of <paramref name="values"/> (ordinal).</summary>
    public TableData Where(string column, params string?[] values)
    {
        var index = ColumnIndex(column);
        return new TableData(Name, Columns, Rows.Where(r => values.Contains(r[index], StringComparer.Ordinal)));
    }

    public TableData Where(Func<IReadOnlyList<string?>, bool> predicate) => new(Name, Columns, Rows.Where(predicate));

    public static TableData Empty(string name, IReadOnlyList<string> columns) => new(name, columns, []);
}
