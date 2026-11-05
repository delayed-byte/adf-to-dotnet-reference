using CreditUnion.Batch.TestData.Canonical;
using static CreditUnion.Batch.TestData.Canonical.CanonicalValue;

namespace CreditUnion.Batch.TestData.KeyStone;

/// <summary>A KeyStone Db2 table as the scenario input files describe it.</summary>
public sealed record Db2TableDefinition(string Name, IReadOnlyList<ColumnDefinition> Columns, IReadOnlyList<string> KeyColumns)
{
    public string FileName => Name + ".csv";
}

/// <summary>
/// The Db2 tables a scenario loads (mirrors db/keystone-db2/migrations). Db2 TIMESTAMP(6) is
/// described with <see cref="ColumnType.DateTime2(int)"/> precision 6: the canonical text is the same.
/// </summary>
public static class KeyStoneTables
{
    private static readonly ColumnType Ts6 = ColumnType.DateTime2(6);
    private static readonly ColumnType Money = ColumnType.Decimal(15, 2);

    public static Db2TableDefinition TxnCode { get; } = new("KS.TXN_CODE",
    [
        new("TXN_CODE", ColumnType.Char(4)),
        new("DESCRIPTION", ColumnType.VarChar(60)),
        new("CATEGORY", ColumnType.Char(3)),
    ], ["TXN_CODE"]);

    public static Db2TableDefinition Member { get; } = new("KS.MEMBER",
    [
        new("MEMBER_NBR", ColumnType.Char(10)),
        new("FIRST_NAME", ColumnType.VarChar(40)),
        new("LAST_NAME", ColumnType.VarChar(40)),
        new("MEMBER_SINCE", ColumnType.Date),
        new("BRANCH_CODE", ColumnType.Char(4)),
        new("STATUS", ColumnType.Char(1)),
        new("LAST_MAINT_TS", Ts6),
    ], ["MEMBER_NBR"]);

    public static Db2TableDefinition Account { get; } = new("KS.ACCOUNT",
    [
        new("ACCOUNT_NBR", ColumnType.Char(12)),
        new("MEMBER_NBR", ColumnType.Char(10)),
        new("ACCOUNT_CATEGORY", ColumnType.Char(1)),
        new("PRODUCT_CODE", ColumnType.Char(6)),
        new("DESCRIPTION", ColumnType.VarChar(60), Nullable: true),
        new("OPEN_DATE", ColumnType.Date),
        new("CLOSE_DATE", ColumnType.Date, Nullable: true),
        new("CURRENT_BALANCE", Money),
        new("AVAILABLE_BALANCE", Money, Nullable: true),
        new("INTEREST_RATE", ColumnType.Decimal(9, 6)),
        new("ACCRUED_INTEREST", ColumnType.Decimal(31, 8)),
        new("STATUS", ColumnType.Char(1)),
        new("LAST_ACTIVITY_DATE", ColumnType.Date),
        new("LAST_MAINT_TS", Ts6),
    ], ["ACCOUNT_NBR"]);

    public static Db2TableDefinition PostedTxn { get; } = new("KS.POSTED_TXN",
    [
        new("TXN_ID", ColumnType.BigInt),
        new("ACCOUNT_NBR", ColumnType.Char(12)),
        new("TXN_CODE", ColumnType.Char(4)),
        new("POSTING_DATE", ColumnType.Date),
        new("EFFECTIVE_DATE", ColumnType.Date),
        new("POSTED_TS", Ts6),
        new("AMOUNT", Money),
        new("DESCRIPTION", ColumnType.VarChar(60), Nullable: true),
        new("CHANNEL", ColumnType.Char(3)),
        new("CARD_PROCESSOR_REF", ColumnType.VarChar(24), Nullable: true),
        new("REVERSAL_OF_TXN_ID", ColumnType.BigInt, Nullable: true),
    ], ["TXN_ID"]);

    public static Db2TableDefinition GlEntry { get; } = new("KS.GL_ENTRY",
    [
        new("GL_ENTRY_ID", ColumnType.BigInt),
        new("GL_ACCOUNT_NBR", ColumnType.Char(10)),
        new("POSTING_DATE", ColumnType.Date),
        new("DEBIT_AMOUNT", Money),
        new("CREDIT_AMOUNT", Money),
        new("SOURCE_CODE", ColumnType.Char(4)),
        new("BATCH_REF", ColumnType.VarChar(20), Nullable: true),
        new("MEMO", ColumnType.VarChar(80), Nullable: true),
        new("CREATED_TS", Ts6),
    ], ["GL_ENTRY_ID"]);

    /// <summary>Parent tables first, so foreign keys hold while loading; reverse it to delete.</summary>
    public static IReadOnlyList<Db2TableDefinition> LoadOrder { get; } = [TxnCode, Member, Account, PostedTxn, GlEntry];

    public static TableData ToTable(Db2TableDefinition table, KeyStoneDataset data)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(data);
        var columns = table.Columns.Select(c => c.Name).ToArray();
        IEnumerable<IReadOnlyList<string?>> rows = table.Name switch
        {
            "KS.TXN_CODE" => data.ExtraTxnCodes.OrderBy(t => t.TxnCode, StringComparer.Ordinal).Select(Row),
            "KS.MEMBER" => data.Members.OrderBy(m => m.MemberNbr, StringComparer.Ordinal).Select(Row),
            "KS.ACCOUNT" => data.Accounts.OrderBy(a => a.AccountNbr, StringComparer.Ordinal).Select(Row),
            "KS.POSTED_TXN" => data.Transactions.OrderBy(t => t.TxnId).Select(Row),
            "KS.GL_ENTRY" => data.GlEntries.OrderBy(g => g.GlEntryId).Select(Row),
            _ => throw new ArgumentException($"Unknown KeyStone table {table.Name}.", nameof(table)),
        };
        return new TableData(table.Name, columns, rows);
    }

    public static string?[] Row(TxnCodeRef t) => [Char(t.TxnCode, 4), t.Description, Char(t.Category, 3)];

    public static string?[] Row(Member m) =>
        [Char(m.MemberNbr, 10), m.FirstName, m.LastName, Date(m.MemberSince), Char(m.BranchCode, 4), Char(m.Status, 1), Timestamp(m.LastMaintTs, 6)];

    public static string?[] Row(Account a) =>
    [
        Char(a.AccountNbr, 12), Char(a.MemberNbr, 10), Char(a.AccountCategory, 1), Char(a.ProductCode, 6), a.Description,
        Date(a.OpenDate), a.CloseDate is { } close ? Date(close) : null, Decimal(a.CurrentBalance, 2),
        a.AvailableBalance is { } available ? Decimal(available, 2) : null, Decimal(a.InterestRate, 6),
        Decimal(a.AccruedInterest, 8), Char(a.Status, 1), Date(a.LastActivityDate), Timestamp(a.LastMaintTs, 6),
    ];

    public static string?[] Row(PostedTxn t) =>
    [
        Integer(t.TxnId), Char(t.AccountNbr, 12), Char(t.TxnCode, 4), Date(t.PostingDate), Date(t.EffectiveDate),
        Timestamp(t.PostedTs, 6), Decimal(t.Amount, 2), t.Description, Char(t.Channel, 3), t.CardProcessorRef,
        t.ReversalOfTxnId is { } reversal ? Integer(reversal) : null,
    ];

    public static string?[] Row(GlEntry g) =>
    [
        Integer(g.GlEntryId), Char(g.GlAccountNbr, 10), Date(g.PostingDate), Decimal(g.DebitAmount, 2), Decimal(g.CreditAmount, 2),
        Char(g.SourceCode, 4), g.BatchRef, g.Memo, Timestamp(g.CreatedTs, 6),
    ];
}
