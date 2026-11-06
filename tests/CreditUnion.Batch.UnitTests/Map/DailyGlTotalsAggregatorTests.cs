using System.Globalization;
using System.Numerics;
using CreditUnion.Batch.Core.Map;
using CreditUnion.Batch.TestData.Golden;
using CreditUnion.Batch.TestHarness.Scenarios;

namespace CreditUnion.Batch.UnitTests.Map;

/// <summary>
/// DF_MapAndAggregateGl as <see cref="DailyGlTotalsAggregator"/> states it (ADR 0011): hand-checked rounding cases,
/// property tests against an integer reference of half-to-even rounding, the join and split rules, and agreement with
/// every scenario's golden core.DailyGlTotals. AT-08 and AT-10 run the same code against SQL Server.
/// </summary>
public sealed class DailyGlTotalsAggregatorTests
{
    private static readonly DateOnly Day = new(2026, 11, 4);

    // The rounding worksheet of scenario map-midpoint-rounding (tests/TestData/golden/map-midpoint-rounding/summary.md).
    [Theory]
    [InlineData("0.50", "0.75", "0.38")]
    [InlineData("0.50", "0.25", "0.12")]
    [InlineData("0.10", "0.25", "0.02")]
    [InlineData("0.30", "0.75", "0.22")]
    [InlineData("0.70", "0.75", "0.52")]
    [InlineData("0.70", "0.25", "0.18")]
    [InlineData("1.10", "0.75", "0.82")]
    [InlineData("1.10", "0.25", "0.28")]
    [InlineData("4.70", "0.75", "3.52")]
    [InlineData("4.70", "0.25", "1.18")]
    [InlineData("2.70", "0.75", "2.02")]
    [InlineData("10.10", "0.25", "2.52")]
    [InlineData("0.33", "0.75", "0.25")]
    [InlineData("-0.50", "0.25", "-0.12")]
    [InlineData("-4.70", "0.25", "-1.18")]
    [InlineData("-0.30", "0.75", "-0.22")]
    [InlineData("0.01", "0.50", "0.00")]
    [InlineData("0.03", "0.50", "0.02")]
    [InlineData("99999999999.99", "1.000000", "99999999999.99")]
    [InlineData("99999999999.99", "0.333333", "33333300000.00")]
    public void An_allocation_is_the_exact_product_rounded_half_to_even(string amount, string ratio, string expected)
    {
        DailyGlTotalsAggregator.Allocate(D(amount), D(ratio)).ShouldBe(D(expected));
    }

    [Fact]
    public void Half_to_even_on_the_exact_decimal_product_tells_the_midpoint_cases_apart()
    {
        // The cases AT-10 tells apart (ADR 0006 semantics 1). Money stays decimal end to end; no binary floating point.
        (4.70m * 0.25m).ShouldBe(1.175m, "the product is exact in decimal");
        DailyGlTotalsAggregator.Allocate(4.70m, 0.25m).ShouldBe(1.18m);
        decimal.Round(0.50m * 0.25m, 2, MidpointRounding.AwayFromZero).ShouldBe(0.13m);
        DailyGlTotalsAggregator.Allocate(0.50m, 0.25m).ShouldBe(0.12m);
    }

    [Fact]
    public void Each_allocation_is_rounded_before_the_sum_not_the_sum_afterwards()
    {
        var map = new[] { M("LPMT", "1300", "C", "0.25"), M("LPMT", "2100", "D", "1") };
        var txns = new[] { T(1, "LPMT", "0.50"), T(2, "LPMT", "0.50"), T(3, "LPMT", "0.50") };

        var totals = DailyGlTotalsAggregator.Aggregate(Day, map, txns);

        // 3 x round(0.125) = 3 x 0.12 = 0.36; rounding the sum 0.375 would give 0.38.
        Row(totals, "1300").CreditTotal.ShouldBe(0.36m);
        Row(totals, "2100").DebitTotal.ShouldBe(1.50m);
    }

    [Fact]
    public void The_midpoint_scenario_gives_the_golden_totals()
    {
        var amounts = new[] { "0.50", "0.10", "0.10", "0.10", "0.30", "0.70", "1.10", "4.70", "2.70", "10.10", "1.00", "0.33" };
        var map = new[] { M("LPMT", "2100", "D", "1"), M("LPMT", "1300", "C", "0.75"), M("LPMT", "4100", "C", "0.25") };
        var txns = amounts.Select((a, i) => T(i + 1, "LPMT", a));

        var totals = DailyGlTotalsAggregator.Aggregate(Day, map, txns);

        Row(totals, "1300").CreditTotal.ShouldBe(16.30m);
        Row(totals, "4100").CreditTotal.ShouldBe(5.43m);
        Row(totals, "2100").DebitTotal.ShouldBe(21.73m);
        Row(totals, "1300").TxnCount.ShouldBe(12);
    }

    [Fact]
    public void Debits_and_credits_split_by_side_and_net_is_debit_minus_credit()
    {
        var map = new[] { M("DEP1", "1010", "D", "1"), M("DEP1", "2100", "C", "1"), M("WDL1", "2100", "D", "1"), M("WDL1", "1010", "C", "1") };
        var txns = new[] { T(1, "DEP1", "100.00"), T(2, "WDL1", "40.25"), T(3, "DEP1", "0.00") };

        var totals = DailyGlTotalsAggregator.Aggregate(Day, map, txns);

        totals.Select(t => t.GlAccountNbr).ShouldBe([Pad("1010"), Pad("2100")], "one row per GL account, ordered");
        totals[0].ShouldBe(new DailyGlTotal(Day, Pad("1010"), 100.00m, 40.25m, 59.75m, 3));
        totals[1].ShouldBe(new DailyGlTotal(Day, Pad("2100"), 40.25m, 100.00m, -59.75m, 3));
    }

    [Fact]
    public void A_transaction_whose_code_debits_and_credits_the_same_account_counts_once_for_it()
    {
        // countDistinct(TxnId) per GL account, not a count of joined rows.
        var map = new[] { M("XFER", "2100", "D", "1"), M("XFER", "2100", "C", "1") };

        var totals = DailyGlTotalsAggregator.Aggregate(Day, map, [T(1, "XFER", "25.00"), T(2, "XFER", "5.00")]);

        totals.ShouldHaveSingleItem().ShouldBe(new DailyGlTotal(Day, Pad("2100"), 30.00m, 30.00m, 0.00m, 2));
    }

    [Fact]
    public void Reversals_and_negative_amounts_are_ordinary_rows_signs_kept()
    {
        // The data flow has no reversal rule: a reversal is a transaction with its own code and amount.
        var map = new[] { M("CPOS", "2100", "D", "1"), M("CPOS", "2400", "C", "1"), M("CRFD", "2400", "D", "1"), M("CRFD", "2100", "C", "1") };
        var txns = new[] { T(1, "CPOS", "12.34"), T(2, "CRFD", "12.34"), T(3, "CPOS", "-1.00") };

        var totals = DailyGlTotalsAggregator.Aggregate(Day, map, txns);

        Row(totals, "2100").ShouldBe(new DailyGlTotal(Day, Pad("2100"), 11.34m, 12.34m, -1.00m, 3));
        Row(totals, "2400").ShouldBe(new DailyGlTotal(Day, Pad("2400"), 12.34m, 11.34m, 1.00m, 3));
    }

    [Fact]
    public void An_allocation_that_rounds_to_zero_still_counts_the_transaction_and_writes_a_row()
    {
        var map = new[] { M("FEE1", "2100", "D", "1"), M("FEE1", "4300", "C", "0.4"), M("FEE1", "4310", "C", "0.6") };

        var totals = DailyGlTotalsAggregator.Aggregate(Day, map, [T(1, "FEE1", "0.01")]);

        Row(totals, "4300").ShouldBe(new DailyGlTotal(Day, Pad("4300"), 0.00m, 0.00m, 0.00m, 1));
        Row(totals, "4310").CreditTotal.ShouldBe(0.01m);
    }

    [Fact]
    public void No_transactions_give_no_rows()
    {
        DailyGlTotalsAggregator.Aggregate(Day, [M("DEP1", "1010", "D", "1")], []).ShouldBeEmpty();
    }

    [Fact]
    public void A_transaction_without_a_mapping_row_fails_instead_of_being_dropped_by_the_inner_join()
    {
        var map = new[] { M("CPOS", "2100", "D", "1"), M("CPOS", "2400", "C", "1") };

        var e = Should.Throw<GlAggregationException>(() =>
            DailyGlTotalsAggregator.Aggregate(Day, map, [T(1, "CPOS", "1.00"), T(2, "P2PD", "2.00"), T(3, "P2PD", "3.00"), T(4, "ATMW", "4.00")]));

        e.Message.ShouldContain("3 transaction(s)");
        e.Message.ShouldContain("ATMW,P2PD");
    }

    [Theory]
    [InlineData("cpos")]
    [InlineData("CPO ")]
    public void The_join_is_exact_case_and_spaces_included(string code)
    {
        Should.Throw<GlAggregationException>(() => DailyGlTotalsAggregator.Aggregate(Day, [M("CPOS", "2100", "D", "1")], [T(1, code, "1.00")]));
    }

    [Fact]
    public void A_row_of_another_posting_date_is_refused()
    {
        var aggregator = new DailyGlTotalsAggregator(Day, [M("CPOS", "2100", "D", "1")]);

        Should.Throw<ArgumentException>(() => aggregator.Add(new PostedAmount(1, Pad4("CPOS"), Day.AddDays(-1), 1.00m)));
    }

    [Fact]
    public void A_mapping_side_other_than_D_or_C_is_refused()
    {
        Should.Throw<ArgumentException>(() => new DailyGlTotalsAggregator(Day, [M("CPOS", "2100", "X", "1")]));
    }

    [Fact]
    public void Property_totals_equal_the_sum_of_individually_rounded_allocations_per_account_and_side()
    {
        foreach (var seed in Seeds)
        {
            CheckProperty_totals_equal_the_sum_of_individually_rounded_allocations_per_account_and_side(seed);
        }
    }

    private static void CheckProperty_totals_equal_the_sum_of_individually_rounded_allocations_per_account_and_side(int seed)
    {
        var (map, txns) = RandomDay(seed);

        var totals = DailyGlTotalsAggregator.Aggregate(Day, map, txns);

        var byCode = map.ToLookup(m => m.TxnCode, StringComparer.Ordinal);
        var expected = txns
            .SelectMany(t => byCode[t.TxnCode].Select(m => (m.GlAccountNbr, m.DrCr, t.TxnId, Amount: ReferenceRound(t.Amount, m.AllocationRatio))))
            .GroupBy(a => a.GlAccountNbr, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var debit = g.Where(a => a.DrCr == "D").Sum(a => a.Amount);
                var credit = g.Where(a => a.DrCr == "C").Sum(a => a.Amount);
                return new DailyGlTotal(Day, g.Key, debit, credit, debit - credit, g.Select(a => a.TxnId).Distinct().Count());
            })
            .ToList();
        totals.ShouldBe(expected, $"seed {seed}");
        totals.ShouldAllBe(t => t.NetAmount == t.DebitTotal - t.CreditTotal);
        totals.ShouldAllBe(t => decimal.Round(t.DebitTotal, 2) == t.DebitTotal && decimal.Round(t.CreditTotal, 2) == t.CreditTotal);
    }

    [Fact]
    public void Property_each_side_of_a_transaction_allocates_its_amount_to_within_half_a_cent_per_allocation()
    {
        foreach (var seed in Seeds)
        {
            CheckProperty_each_side_of_a_transaction_allocates_its_amount_to_within_half_a_cent_per_allocation(seed);
        }
    }

    private static void CheckProperty_each_side_of_a_transaction_allocates_its_amount_to_within_half_a_cent_per_allocation(int seed)
    {
        var (map, txns) = RandomDay(seed);
        var byCodeAndSide = map.ToLookup(m => (m.TxnCode, m.DrCr));

        foreach (var t in txns)
        {
            foreach (var side in new[] { "D", "C" })
            {
                var allocations = byCodeAndSide[(t.TxnCode, side)].ToList();
                var allocated = allocations.Sum(m => DailyGlTotalsAggregator.Allocate(t.Amount, m.AllocationRatio));
                Math.Abs(allocated - t.Amount).ShouldBeLessThanOrEqualTo(0.005m * allocations.Count, $"seed {seed} txn {t.TxnId} side {side}");
                if (allocations.Count == 1)
                {
                    allocated.ShouldBe(t.Amount, "a single allocation of ratio 1 is the amount itself");
                }
            }
        }
    }

    [Fact]
    public void Property_the_result_does_not_depend_on_the_order_of_the_rows()
    {
        foreach (var seed in Seeds)
        {
            CheckProperty_the_result_does_not_depend_on_the_order_of_the_rows(seed);
        }
    }

    private static void CheckProperty_the_result_does_not_depend_on_the_order_of_the_rows(int seed)
    {
        var (map, txns) = RandomDay(seed);
        var shuffled = txns.ToArray();
        new Random(seed ^ 0x5EED).Shuffle(shuffled);
        var reversedMap = map.AsEnumerable().Reverse().ToList();

        DailyGlTotalsAggregator.Aggregate(Day, reversedMap, shuffled).ShouldBe(DailyGlTotalsAggregator.Aggregate(Day, map, txns), $"seed {seed}");
    }

    [Fact]
    public void Property_debits_and_credits_over_all_accounts_differ_only_by_rounding()
    {
        foreach (var seed in Seeds)
        {
            CheckProperty_debits_and_credits_over_all_accounts_differ_only_by_rounding(seed);
        }
    }

    private static void CheckProperty_debits_and_credits_over_all_accounts_differ_only_by_rounding(int seed)
    {
        // Each code allocates 100% to each side, so the day balances up to the per-allocation rounding.
        var (map, txns) = RandomDay(seed);
        var allocationsPerTxn = map.GroupBy(m => m.TxnCode, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var totals = DailyGlTotalsAggregator.Aggregate(Day, map, txns);

        var imbalance = Math.Abs(totals.Sum(t => t.NetAmount));
        imbalance.ShouldBeLessThanOrEqualTo(0.005m * txns.Sum(t => allocationsPerTxn[t.TxnCode]), $"seed {seed}");
    }

    public static TheoryData<string> GoldenScenarios => [.. Scenario.CommittedNames.Where(n => Scenario.Load(n).Golden.Tables.Any(t => t.Name == GoldenTables.DailyGlTotals.Name && t.RowCount > 0))];

    [Theory]
    [MemberData(nameof(GoldenScenarios))]
    public void Every_golden_DailyGlTotals_date_is_reproduced_from_the_golden_transactions_and_mapping(string name)
    {
        var scenario = Scenario.Load(name);
        var golden = scenario.GoldenTable(GoldenTables.DailyGlTotals.Name);
        var postings = scenario.GoldenTable(GoldenTables.PostedTransaction.Name);
        var glMap = scenario.GoldenTable(GoldenTables.TxnCodeGlMap.Name);
        var map = glMap.Rows.Select(r => new GlMapping(r[glMap.ColumnIndex("TxnCode")]!, r[glMap.ColumnIndex("GlAccountNbr")]!, r[glMap.ColumnIndex("DrCr")]!,
            D(r[glMap.ColumnIndex("AllocationRatio")]!))).ToList();
        var dates = golden.Rows.Select(r => DateOnly.ParseExact(r[golden.ColumnIndex("BusinessDate")]!, "yyyy-MM-dd", CultureInfo.InvariantCulture)).Distinct().ToList();

        foreach (var date in dates)
        {
            var txns = postings.Rows
                .Select(r => new PostedAmount(long.Parse(r[postings.ColumnIndex("TxnId")]!, CultureInfo.InvariantCulture), r[postings.ColumnIndex("TxnCode")]!,
                    DateOnly.ParseExact(r[postings.ColumnIndex("PostingDate")]!, "yyyy-MM-dd", CultureInfo.InvariantCulture), D(r[postings.ColumnIndex("Amount")]!)))
                .Where(t => t.PostingDate == date);
            var expected = golden.Rows.Where(r => r[golden.ColumnIndex("BusinessDate")] == date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Select(r => new DailyGlTotal(date, r[golden.ColumnIndex("GlAccountNbr")]!, D(r[golden.ColumnIndex("DebitTotal")]!), D(r[golden.ColumnIndex("CreditTotal")]!),
                    D(r[golden.ColumnIndex("NetAmount")]!), int.Parse(r[golden.ColumnIndex("TxnCount")]!, CultureInfo.InvariantCulture)))
                .OrderBy(t => t.GlAccountNbr, StringComparer.Ordinal).ToList();

            DailyGlTotalsAggregator.Aggregate(date, map, txns).ShouldBe(expected, $"{name} {date:yyyy-MM-dd}");
        }
    }

    private static IEnumerable<int> Seeds => Enumerable.Range(1, 500);

    /// <summary>Half-to-even rounding of cents x millionths to cents, in integers only: an independent reference for <see cref="DailyGlTotalsAggregator.Allocate"/>.</summary>
    private static decimal ReferenceRound(decimal amount, decimal ratio)
    {
        var product = new BigInteger(amount * 100m) * new BigInteger(ratio * 1_000_000m);
        var quotient = BigInteger.DivRem(BigInteger.Abs(product), 1_000_000, out var remainder);
        if (remainder > 500_000 || (remainder == 500_000 && !quotient.IsEven))
        {
            quotient += 1;
        }

        return (decimal)(quotient * product.Sign) / 100m;
    }

    /// <summary>
    /// A random mapping (each code splits each side over one to three GL accounts in millionths that sum to exactly 1,
    /// with accounts that may appear on both sides) and up to 300 transactions with amounts chosen to hit midpoints often.
    /// </summary>
    private static (List<GlMapping> Map, List<PostedAmount> Txns) RandomDay(int seed)
    {
        var random = new Random(seed);
        var accounts = Enumerable.Range(0, 8).Select(i => (1000 + (i * 100)).ToString(CultureInfo.InvariantCulture)).ToArray();
        var codes = Enumerable.Range(0, random.Next(1, 8)).Select(i => "C" + i.ToString("D3", CultureInfo.InvariantCulture)).ToArray();
        var map = new List<GlMapping>();
        foreach (var code in codes)
        {
            foreach (var side in new[] { "D", "C" })
            {
                var parts = random.Next(1, 4);
                var chosen = accounts.OrderBy(_ => random.Next()).Take(parts).ToArray();
                var remaining = 1_000_000;
                for (var p = 0; p < parts; p++)
                {
                    var micro = p == parts - 1 ? remaining : random.Next(1, remaining - (parts - p - 1));
                    remaining -= micro;
                    map.Add(M(code, chosen[p], side, (micro / 1_000_000m).ToString(CultureInfo.InvariantCulture)));
                }
            }
        }

        var quarterRatios = new[] { 0.25m, 0.5m, 0.75m, 0.125m };
        if (random.Next(2) == 0)
        {
            // Midpoint-heavy variant: replace one code's sides with quarter splits.
            var code = codes[0];
            map.RemoveAll(m => m.TxnCode == Pad4(code));
            var r = quarterRatios[random.Next(quarterRatios.Length)];
            map.AddRange([M(code, accounts[0], "D", r.ToString(CultureInfo.InvariantCulture)), M(code, accounts[1], "D", (1 - r).ToString(CultureInfo.InvariantCulture)), M(code, accounts[2], "C", "1")]);
        }

        var txns = Enumerable.Range(1, random.Next(0, 301)).Select(i =>
        {
            var cents = random.Next(4) switch
            {
                0 => random.Next(-500, 500) * 10L + 5,       // x.x5: midpoints for quarter splits
                1 => random.Next(-100_000, 100_000) * 50L,   // whole halves
                2 => random.NextInt64(-9_999_999_999L, 9_999_999_999L),
                _ => random.Next(-20_000, 20_000),
            };
            return new PostedAmount(i, Pad4(codes[random.Next(codes.Length)]), Day, cents / 100m);
        }).ToList();
        return (map, txns);
    }

    private static DailyGlTotal Row(IReadOnlyList<DailyGlTotal> totals, string account) => totals.Single(t => t.GlAccountNbr == Pad(account));

    private static GlMapping M(string code, string account, string side, string ratio) => new(Pad4(code), Pad(account), side, D(ratio));

    private static PostedAmount T(long id, string code, string amount) => new(id, Pad4(code), Day, D(amount));

    private static string Pad(string account) => account.PadRight(10);

    private static string Pad4(string code) => code.PadRight(4);

    private static decimal D(string text) => decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);
}
