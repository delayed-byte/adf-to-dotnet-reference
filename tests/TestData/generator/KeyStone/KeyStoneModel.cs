using System.Data.SqlTypes;

namespace CreditUnion.Batch.TestData.KeyStone;

// Rows of the synthetic KeyStone Db2 schema (db/keystone-db2), in Db2 storage form: CHAR values are
// space-padded to their declared length, timestamps are KeyStone local wall-clock time with
// microsecond precision and DateTimeKind.Unspecified (ADR 0005).

public sealed record Member(
    string MemberNbr,
    string FirstName,
    string LastName,
    DateOnly MemberSince,
    string BranchCode,
    string Status,
    DateTime LastMaintTs);

public sealed record Account(
    string AccountNbr,
    string MemberNbr,
    string AccountCategory,
    string ProductCode,
    string? Description,
    DateOnly OpenDate,
    DateOnly? CloseDate,
    decimal CurrentBalance,
    decimal? AvailableBalance,
    decimal InterestRate,
    SqlDecimal AccruedInterest,
    string Status,
    DateOnly LastActivityDate,
    DateTime LastMaintTs);

public sealed record PostedTxn(
    long TxnId,
    string AccountNbr,
    string TxnCode,
    DateOnly PostingDate,
    DateOnly EffectiveDate,
    DateTime PostedTs,
    decimal Amount,
    string? Description,
    string Channel,
    string? CardProcessorRef,
    long? ReversalOfTxnId);

public sealed record GlEntry(
    long GlEntryId,
    string GlAccountNbr,
    DateOnly PostingDate,
    decimal DebitAmount,
    decimal CreditAmount,
    string SourceCode,
    string? BatchRef,
    string? Memo,
    DateTime CreatedTs);

public sealed record TxnCodeRef(string TxnCode, string Description, string Category);

/// <summary>Everything a scenario loads into Db2, on top of the reference data seeded by migration 0006.</summary>
public sealed class KeyStoneDataset
{
    public List<Member> Members { get; } = [];

    public List<Account> Accounts { get; } = [];

    public List<PostedTxn> Transactions { get; } = [];

    public List<GlEntry> GlEntries { get; } = [];

    /// <summary>Transaction codes added for the scenario (for example a code KeyBridge does not map yet).</summary>
    public List<TxnCodeRef> ExtraTxnCodes { get; } = [];
}
