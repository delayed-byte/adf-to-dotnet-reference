namespace CreditUnion.Batch.KeyStone.Staging;

/// <summary>The SQL Server column types of the stg.Ks* tables (ADR 0005 and 0003), which decide how each Db2 value is read.</summary>
public enum StagingKind
{
    FixedChar,
    VarChar,
    ExactDecimal,
    Date,
    DateTime2,
    BigInt,
    Integer32,
}

/// <summary>One column of a staging table as SQL Server reports it. <paramref name="Length"/> is the column size in bytes for CHAR/VARCHAR.</summary>
public sealed record StagingColumn(string Name, StagingKind Kind, int Length, int Precision, int Scale);
