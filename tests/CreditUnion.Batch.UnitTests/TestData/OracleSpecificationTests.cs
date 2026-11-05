using System.Text;
using CreditUnion.Batch.TestData.Card;
using CreditUnion.Batch.TestData.KeyStone;
using CreditUnion.Batch.TestData.Oracle;
using CreditUnion.Batch.TestData.Reporting;
using CreditUnion.Batch.TestData.Scenarios;

namespace CreditUnion.Batch.UnitTests.TestData;

/// <summary>
/// Hand-checked cases for the rules the oracle derives golden outputs from. Each expected value was
/// worked out from the PRD and contracts, not from the code under test.
/// </summary>
public sealed class OracleSpecificationTests
{
    private static readonly DateOnly Day = new(2026, 11, 4);

    [Theory]
    [InlineData("0.50", "0.25", "0.12")] // 0.125: 2 is even, stays (half away from zero would give 0.13)
    [InlineData("0.50", "0.75", "0.38")] // 0.375: 7 is odd, goes up
    [InlineData("0.10", "0.25", "0.02")] // 0.025
    [InlineData("4.70", "0.25", "1.18")] // 1.175 exactly in decimal (a double would hold 1.17499999...)
    [InlineData("10.70", "0.25", "2.68")] // 2.675 exactly in decimal (a double would hold 2.67499999...)
    [InlineData("0.33", "0.25", "0.08")] // 0.0825: not a midpoint
    [InlineData("12.34", "1.00", "12.34")]
    public void Allocation_uses_bankers_rounding_on_the_exact_decimal_product(string amount, string ratio, string expected)
    {
        GlAllocation.Allocate(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(ratio, System.Globalization.CultureInfo.InvariantCulture))
            .ShouldBe(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Daily_totals_round_each_allocation_before_summing_and_count_distinct_transactions()
    {
        // Three payments of 0.10 split 75/25: each interest part is 0.025 -> 0.02, so 4100 totals 0.06;
        // rounding the sum (0.075) instead would give 0.08.
        var txns = Enumerable.Range(1, 3).Select(i => Txn(i, "LPMT", 0.10m)).Append(Txn(9, "DEP1", 5.00m)).ToList();
        TxnCodeGlMapRow[] map =
        [
            Map("LPMT", "2100", "D", 1m), Map("LPMT", "1300", "C", 0.75m), Map("LPMT", "4100", "C", 0.25m),
            Map("DEP1", "1010", "D", 1m), Map("DEP1", "2100", "C", 1m),
        ];

        var totals = GlAllocation.DailyTotals(Day, txns, map).ToDictionary(t => t.GlAccountNbr.TrimEnd());

        totals["4100"].CreditTotal.ShouldBe(0.06m);
        totals["1300"].CreditTotal.ShouldBe(0.24m);
        totals["2100"].ShouldBe(new DailyGlTotalRow(Day, "2100      ", 0.30m, 5.00m, -4.70m, 4));
        totals.Keys.Order().ShouldBe(["1010", "1300", "2100", "4100"]);
    }

    [Fact]
    public void Settlement_rules_accept_the_contract_example_file()
    {
        const string example = "H,TESTPROC,20261104,0001\nD,TP20261104000000000001,20261103,400000,1234,25.00,SALE,SYNTHETIC GROCER,5411,USD\n" +
            "D,TP20261104000000000002,20261104,400000,9876,10.50,RFND,\"SYNTHETIC CAFE, INC\",5814,USD\nT,2,35.50\n";

        var result = Validate(example);

        result.IsValid.ShouldBeTrue();
        result.DetailRowCount.ShouldBe(2);
        result.DetailTotalAmount.ShouldBe(35.50m);
        result.ErrorSummary.ShouldBeNull();
    }

    [Fact]
    public void Settlement_rules_report_every_problem_with_the_procedures_wording()
    {
        const string bad = "H,TESTPROC,20261103,0001\nD,TP1,2026-11-04,40000,12,12.555,SALE,,54,EUR\nD,TP2,20261104,400000,1234,1.00,SALE,,,USD\n" +
            "D,TP2,20261104,400000,1234,1.00,SALE,,,USD\nX,junk\nT,5,9.99\n";

        var result = Validate(bad);

        result.IsValid.ShouldBeFalse();
        result.Errors.Select(e => $"{e.RecordType}|{e.ProcessorRefNbr}|{e.ErrorCode}|{e.ErrorMessage}").ShouldBe(
        [
            "H||HEADER_DATE_MISMATCH|Header settlement date 20261103 does not match business date 20261104.",
            "D|TP1|ROW_SCHEMA_INVALID|Invalid field(s): TxnDate, CardBin, CardLast4, Amount, Mcc, CurrencyCode",
            "D|TP2|DUPLICATE_PROCESSOR_REF|Processor reference appears 2 times in the file.",
            "T||TRAILER_COUNT_MISMATCH|Trailer record count 5 does not match 3 detail records.",
            "X||RECORD_TYPE_INVALID|Unknown record type 'X'; expected H, D or T.",
        ]);
        result.ErrorSummary.ShouldBe("DUPLICATE_PROCESSOR_REF x1; HEADER_DATE_MISMATCH x1; RECORD_TYPE_INVALID x1; ROW_SCHEMA_INVALID x1; TRAILER_COUNT_MISMATCH x1");
    }

    [Fact]
    public void Settlement_rules_check_the_trailer_total_only_when_rows_are_well_formed()
    {
        var result = Validate("H,TESTPROC,20261104,0001\nD,TP1,20261104,400000,1234,1.00,SALE,,,USD\nT,1,2.00\n");

        result.Errors.Single().ErrorMessage.ShouldBe("Trailer amount total 2.00 does not match detail total 1.00.");
        Validate("H,TESTPROC,20261104,0001\nD,TP1,20261104,400000,1234,1.00,SALE,,,USD\n").Errors.Single().ErrorCode.ShouldBe("TRAILER_MISSING");
    }

    [Fact]
    public void Trigger_business_date_is_the_previous_pacific_day_across_both_dst_changes()
    {
        // 03:00 Pacific is 11:00Z under PST (UTC-8) and 10:00Z under PDT (UTC-7). Hand-written.
        ScenarioCatalog.BusinessDateFor(new DateTimeOffset(2026, 3, 8, 10, 0, 0, TimeSpan.Zero)).ShouldBe(new DateOnly(2026, 3, 7));
        ScenarioCatalog.BusinessDateFor(new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.Zero)).ShouldBe(new DateOnly(2026, 10, 31));
        ScenarioCatalog.BusinessDateFor(new DateTimeOffset(2026, 11, 2, 7, 59, 59, TimeSpan.Zero)).ShouldBe(new DateOnly(2026, 10, 31));

        var schedule = ScenarioCatalog.Get("dst-transitions").Schedule!;
        schedule.Select(s => s.FireTimeUtc).ShouldBe(
        [
            "2026-03-06T11:00:00Z", "2026-03-07T11:00:00Z", "2026-03-08T10:00:00Z", "2026-03-09T10:00:00Z", "2026-03-10T10:00:00Z",
            "2026-10-30T10:00:00Z", "2026-10-31T10:00:00Z", "2026-11-01T11:00:00Z", "2026-11-02T11:00:00Z", "2026-11-03T11:00:00Z",
        ]);
        schedule.Select(s => s.LocalDate).Distinct().Count().ShouldBe(schedule.Count, "the trigger fires exactly once per local date");
    }

    private static SettlementValidationResult Validate(string text) =>
        SettlementValidator.Validate(SettlementValidator.Land(Encoding.UTF8.GetBytes(text)), Day);

    private static PostedTxn Txn(long id, string code, decimal amount) =>
        new(id, "990000000001", code, Day, Day, Day.ToDateTime(TimeOnly.MinValue), amount, null, "SYS", null, null);

    private static TxnCodeGlMapRow Map(string code, string account, string side, decimal ratio) => new(code, account.PadRight(10), side, ratio, null, "v");
}
