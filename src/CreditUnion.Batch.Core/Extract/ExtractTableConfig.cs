namespace CreditUnion.Batch.Core.Extract;

/// <summary>
/// One enabled row of <c>ops.ExtractConfig</c>: a KeyStone table copied into a staging table for one
/// business date (the Lookup Extract Config output of PL_ExtractKeyStone).
/// </summary>
public sealed record ExtractTableConfig(
    string SourceName,
    string SourceSchema,
    string SourceTable,
    string SourceColumns,
    string KeyColumns,
    string DateColumn,
    string TargetSchema,
    string TargetTable)
{
    /// <summary><c>schema.table</c> of the staging table; the ObjectName of the CopyToStaging event.</summary>
    public string TargetName => $"{TargetSchema}.{TargetTable}";

    /// <summary><c>schema.table</c> of the KeyStone source.</summary>
    public string SourceObject => $"{SourceSchema}.{SourceTable}";
}
