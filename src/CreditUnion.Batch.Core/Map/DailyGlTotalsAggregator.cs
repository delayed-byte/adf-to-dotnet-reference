namespace CreditUnion.Batch.Core.Map;

/// <summary>
/// DF_MapAndAggregateGl in C# (ADR 0011), the one statement of the rule in <c>src</c>:
/// <list type="number">
/// <item><c>JoinGlMap</c>: inner join of the business date's transactions to the mapping on TxnCode, exact match
/// (ordinal, case-sensitive, spaces significant: <c>matchType 'exact'</c>, <c>ignoreSpaces false</c>).</item>
/// <item><c>DeriveAllocatedAmounts</c>: <c>AllocatedAmount = round(Amount * AllocationRatio, 2)</c> with banker's rounding,
/// computed on the exact decimal product (<see cref="MidpointRounding.ToEven"/>, never a double), once per allocation.</item>
/// <item><c>SplitBySide</c>: the amount goes to DebitAmount for DrCr 'D' and to CreditAmount for 'C'.</item>
/// <item><c>AggregateByGlAccount</c>: per GL account, the sums of the rounded allocations and the count of distinct TxnIds.</item>
/// <item><c>DeriveTotals</c>: the business date and NetAmount = DebitTotal - CreditTotal.</item>
/// </list>
/// Rows are streamed through <see cref="Add"/>; memory is proportional to the mapping and the number of GL accounts,
/// not to the number of transactions. The input order does not matter. Each TxnId must occur once (it is the key of
/// <c>core.PostedTransaction</c>), which is what makes counting per transaction equal to <c>countDistinct(TxnId)</c>.
/// </summary>
public sealed class DailyGlTotalsAggregator
{
    private readonly DateOnly businessDate;
    private readonly Dictionary<string, CodeRule> rules;
    private readonly Dictionary<string, AccountTotals> totals = new(StringComparer.Ordinal);
    private readonly SortedSet<string> unmatchedCodes = new(StringComparer.Ordinal);
    private long unmatchedTransactions;

    public DailyGlTotalsAggregator(DateOnly businessDate, IEnumerable<GlMapping> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        this.businessDate = businessDate;
        var rows = map.ToList();
        foreach (var row in rows)
        {
            if (row.DrCr is not (GlMapping.Debit or GlMapping.Credit))
            {
                throw new ArgumentException($"Mapping row for transaction code '{row.TxnCode.TrimEnd(' ')}' has side '{row.DrCr}'; only 'D' and 'C' exist (CK_TxnCodeGlMap_DrCr).", nameof(map));
            }
        }

        rules = rows.GroupBy(r => r.TxnCode, StringComparer.Ordinal).ToDictionary(
            g => g.Key,
            g => new CodeRule([.. g], [.. g.Select(r => r.GlAccountNbr).Distinct(StringComparer.Ordinal)]),
            StringComparer.Ordinal);
    }

    /// <summary>Transactions added so far that joined to the mapping.</summary>
    public long TransactionCount { get; private set; }

    /// <summary>
    /// One allocation: <c>round(amount * allocationRatio, 2)</c>, half to even, on the exact decimal product. A
    /// decimal(15,2) amount times a decimal(9,6) ratio has at most 24 significant digits, so the product is exact in
    /// <see cref="decimal"/>. Binary floating point cannot hold 1.175 (4.70 x 0.25) exactly, so it is never used for money.
    /// </summary>
    public static decimal Allocate(decimal amount, decimal allocationRatio) =>
        decimal.Round(amount * allocationRatio, 2, MidpointRounding.ToEven);

    /// <summary>The whole data flow over in-memory inputs; throws <see cref="GlAggregationException"/> like <see cref="Complete"/>.</summary>
    public static IReadOnlyList<DailyGlTotal> Aggregate(DateOnly businessDate, IEnumerable<GlMapping> map, IEnumerable<PostedAmount> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        var aggregator = new DailyGlTotalsAggregator(businessDate, map);
        foreach (var transaction in transactions)
        {
            aggregator.Add(transaction);
        }

        return aggregator.Complete();
    }

    public void Add(PostedAmount transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.PostingDate != businessDate)
        {
            throw new ArgumentException($"Transaction {transaction.TxnId} was posted on {transaction.PostingDate:yyyy-MM-dd}, not on the business date {businessDate:yyyy-MM-dd}.", nameof(transaction));
        }

        if (!rules.TryGetValue(transaction.TxnCode, out var rule))
        {
            unmatchedCodes.Add(transaction.TxnCode.TrimEnd(' '));
            unmatchedTransactions++;
            return;
        }

        foreach (var allocation in rule.Allocations)
        {
            var amount = Allocate(transaction.Amount, allocation.AllocationRatio);
            var account = Account(allocation.GlAccountNbr);
            if (allocation.DrCr == GlMapping.Debit)
            {
                account.Debit += amount;
            }
            else
            {
                account.Credit += amount;
            }
        }

        foreach (var glAccount in rule.Accounts)
        {
            Account(glAccount).TxnCount++;
        }

        TransactionCount++;
    }

    /// <summary>
    /// One row per GL account touched, ordered by GL account. Throws <see cref="GlAggregationException"/> when a
    /// transaction had no mapping: ADF's inner join would drop it silently, but the pipeline's unmapped-code check
    /// makes that impossible, so reaching it means the mapping changed underneath the run (ADR 0011).
    /// </summary>
    public IReadOnlyList<DailyGlTotal> Complete()
    {
        if (unmatchedCodes.Count > 0)
        {
            throw new GlAggregationException(
                $"{unmatchedTransactions} transaction(s) posted on {businessDate:yyyy-MM-dd} have no row in ref.TxnCodeGlMap at aggregation time " +
                $"(transaction codes: {string.Join(',', unmatchedCodes)}). Nothing was written; the inner join would have dropped them.");
        }

        return [.. totals.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t =>
            new DailyGlTotal(businessDate, t.Key, t.Value.Debit, t.Value.Credit, t.Value.Debit - t.Value.Credit, t.Value.TxnCount))];
    }

    private AccountTotals Account(string glAccountNbr)
    {
        if (!totals.TryGetValue(glAccountNbr, out var account))
        {
            account = new AccountTotals();
            totals.Add(glAccountNbr, account);
        }

        return account;
    }

    private sealed record CodeRule(IReadOnlyList<GlMapping> Allocations, IReadOnlyList<string> Accounts);

    private sealed class AccountTotals
    {
        public decimal Debit { get; set; }

        public decimal Credit { get; set; }

        public int TxnCount { get; set; }
    }
}
