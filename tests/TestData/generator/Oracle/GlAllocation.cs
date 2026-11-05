using CreditUnion.Batch.TestData.KeyStone;
using CreditUnion.Batch.TestData.Reporting;

namespace CreditUnion.Batch.TestData.Oracle;

/// <summary>
/// The specification of DF_MapAndAggregateGl (PRD, PL_MapAndAggregate step 4; ADR 0006):
/// inner-join the business date's transactions to the mapping on TxnCode; for every joined row derive
/// <c>AllocatedAmount = round(Amount * AllocationRatio, 2)</c> with banker's rounding
/// (<see cref="MidpointRounding.ToEven"/>) on the exact decimal product; put it on the debit or credit
/// side per DrCr; then sum per GL account (rounding happens per allocation, before the sum) and count
/// distinct transactions. NetAmount = DebitTotal - CreditTotal.
/// </summary>
public static class GlAllocation
{
    public static decimal Allocate(decimal amount, decimal allocationRatio) =>
        decimal.Round(amount * allocationRatio, 2, MidpointRounding.ToEven);

    public static IReadOnlyList<DailyGlTotalRow> DailyTotals(DateOnly businessDate, IEnumerable<PostedTxn> transactions, IReadOnlyList<TxnCodeGlMapRow> map)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(map);
        var byCode = map.ToLookup(m => m.TxnCode, StringComparer.Ordinal);

        var allocations =
            from t in transactions
            where t.PostingDate == businessDate
            from m in byCode[t.TxnCode]
            let amount = Allocate(t.Amount, m.AllocationRatio)
            select (m.GlAccountNbr, t.TxnId, Debit: m.DrCr == "D" ? amount : 0m, Credit: m.DrCr == "C" ? amount : 0m);

        return [.. allocations
            .GroupBy(a => a.GlAccountNbr, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var debit = g.Sum(a => a.Debit);
                var credit = g.Sum(a => a.Credit);
                return new DailyGlTotalRow(businessDate, g.Key, debit, credit, debit - credit, g.Select(a => a.TxnId).Distinct().Count());
            })];
    }
}
