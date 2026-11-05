namespace CreditUnion.Batch.TestData.Canonical;

/// <summary>SQL type families that appear in KeyStone inputs and golden tables.</summary>
public enum SqlKind
{
    Char,
    VarChar,
    NVarChar,
    Decimal,
    Date,
    DateTime2,
    BigInt,
    Int,
    Bit,
}

/// <summary>
/// A column type. The canonical text form of a value depends only on the type (ADR 0006):
/// CHAR values keep their padding, DECIMAL values carry exactly <see cref="Scale"/> fraction digits,
/// DATETIME2/TIMESTAMP values carry exactly <see cref="Precision"/> fraction digits.
/// </summary>
public sealed record ColumnType(SqlKind Kind, int Length = 0, int Precision = 0, int Scale = 0)
{
    public static ColumnType Date { get; } = new(SqlKind.Date);

    public static ColumnType BigInt { get; } = new(SqlKind.BigInt);

    public static ColumnType Int { get; } = new(SqlKind.Int);

    public static ColumnType Bit { get; } = new(SqlKind.Bit);

    public static ColumnType Char(int length) => new(SqlKind.Char, Length: length);

    public static ColumnType VarChar(int length) => new(SqlKind.VarChar, Length: length);

    public static ColumnType NVarChar(int length) => new(SqlKind.NVarChar, Length: length);

    public static ColumnType Decimal(int precision, int scale) => new(SqlKind.Decimal, Precision: precision, Scale: scale);

    public static ColumnType DateTime2(int precision) => new(SqlKind.DateTime2, Precision: precision);

    public bool IsText => Kind is SqlKind.Char or SqlKind.VarChar or SqlKind.NVarChar;

    public override string ToString() => Kind switch
    {
        SqlKind.Char or SqlKind.VarChar or SqlKind.NVarChar => $"{Kind.ToString().ToUpperInvariant()}({Length})",
        SqlKind.Decimal => $"DECIMAL({Precision},{Scale})",
        SqlKind.DateTime2 => $"DATETIME2({Precision})",
        _ => Kind.ToString().ToUpperInvariant(),
    };
}

/// <summary>A named, typed column.</summary>
public sealed record ColumnDefinition(string Name, ColumnType Type, bool Nullable = false);
