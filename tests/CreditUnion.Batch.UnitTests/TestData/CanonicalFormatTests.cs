using System.Data.SqlTypes;
using CreditUnion.Batch.TestData;
using CreditUnion.Batch.TestData.Canonical;

namespace CreditUnion.Batch.UnitTests.TestData;

/// <summary>Self-tests of the golden file format, checksum and comparison (ADR 0006).</summary>
public sealed class CanonicalFormatTests
{
    [Fact]
    public void SplitMix64_matches_the_published_reference_sequence()
    {
        var rng = new DeterministicRandom(1234567);
        ulong[] expected = [6457827717110365317, 3203168211198807973, 9817491932198370423, 4593380528125082431, 16408922859458223821];
        expected.Select(_ => rng.NextUInt64()).ShouldBe(expected);
    }

    [Fact]
    public void Forked_streams_depend_only_on_the_seed_and_label()
    {
        var a = new DeterministicRandom(42);
        var b = new DeterministicRandom(42);
        b.NextUInt64();
        a.Fork("day/2026-11-04").NextUInt64().ShouldBe(b.Fork("day/2026-11-04").NextUInt64());
        a.Fork("x").NextUInt64().ShouldNotBe(a.Fork("y").NextUInt64());
    }

    [Fact]
    public void Csv_round_trips_nulls_empty_strings_padding_quotes_and_line_breaks()
    {
        var table = new TableData("t", ["A", "B"],
        [
            ["\\N", null], [string.Empty, " lead"], ["trail  ", "a,b"], ["say \"hi\"", "LINE1\nLINE2"], ["日本 Ω", "x|y\\z"],
        ]);

        var text = CanonicalCsv.Write(table);
        var back = CanonicalCsv.Read("t", text);

        back.Rows.ShouldBe(table.Rows);
        text.ShouldStartWith("A,B\n\"\\N\",\\N\n\"\",\" lead\"\n\"trail  \",\"a,b\"\n");
        text.ShouldNotContain("\r");
    }

    [Fact]
    public void Csv_reader_rejects_crlf_line_endings()
    {
        Should.Throw<FormatException>(() => CanonicalCsv.Read("t", "A,B\r\n1,2\r\n")).Message.ShouldContain("LF");
    }

    [Fact]
    public void Checksum_matches_the_known_answer_from_the_specification()
    {
        // Computed independently: sha256("Id|Name\n1|a\n2|\N\n3|x\|y\n") with Python hashlib.
        var table = new TableData("t", ["Id", "Name"], [["3", "x|y"], ["1", "a"], ["2", null]]);
        TableChecksum.Compute(table).ShouldBe("94b9cc79be1ac11cb79db5e74caa7fcd06dd121913fe53bd78d4b8169712142a");
    }

    [Fact]
    public void Checksum_is_order_independent_but_sensitive_to_every_value_and_to_null()
    {
        List<IReadOnlyList<string?>> rows = [["1", "a"], ["2", "b"], ["3", null]];
        var baseline = TableChecksum.Compute(new TableData("t", ["Id", "V"], rows));

        TableChecksum.Compute(new TableData("t", ["Id", "V"], Enumerable.Reverse(rows))).ShouldBe(baseline);
        TableChecksum.Compute(new TableData("t", ["Id", "V"], [["1", "a"], ["2", "b"], ["3", "\\N"]])).ShouldNotBe(baseline);
        TableChecksum.Compute(new TableData("t", ["Id", "V"], [["1", "a"], ["2", "b"], ["3", string.Empty]])).ShouldNotBe(baseline);
        TableChecksum.Compute(new TableData("t", ["Id", "V"], [["1", "a "], ["2", "b"], ["3", null]])).ShouldNotBe(baseline);
        TableChecksum.Compute(new TableData("t", ["Id", "W"], rows)).ShouldNotBe(baseline);
        TableChecksum.Compute(new TableData("t", ["Id", "V"], [.. rows, rows[0]])).ShouldNotBe(baseline);
    }

    [Fact]
    public void Keyed_comparison_reports_the_first_20_of_all_mismatches_with_the_total()
    {
        var expected = new TableData("core.T", ["Id", "Amount", "Note"], Enumerable.Range(1, 30).Select(i => new[] { i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), "1.00", "x" }));
        var actual = new TableData("core.T", ["Id", "Amount", "Note"], Enumerable.Range(1, 30).Select(i => new[] { i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), "1.01", "x" }));

        var result = TableComparison.Compare(expected, actual, ["Id"]);

        result.IsMatch.ShouldBeFalse();
        result.TotalMismatches.ShouldBe(30);
        result.Mismatches.Count.ShouldBe(20);
        result.Mismatches[0].ShouldBe("[Id='01'] Amount: expected '1.00', actual '1.01'");
        result.Report().ShouldContain("30 mismatch(es), first 20 shown");
    }

    [Fact]
    public void Keyed_comparison_reports_missing_unexpected_and_null_differences()
    {
        var expected = new TableData("t", ["Id", "V"], [["1", "a"], ["2", null], ["3", "c"]]);
        var actual = new TableData("t", ["Id", "V"], [["1", "a"], ["2", string.Empty], ["4", "d"]]);

        var result = TableComparison.Compare(expected, actual, ["Id"]);

        result.Mismatches.ShouldBe(["[Id='2'] V: expected NULL, actual ''", "missing row [Id='3']", "unexpected row [Id='4']"]);
    }

    [Fact]
    public void Keyless_comparison_treats_rows_as_a_multiset()
    {
        var expected = new TableData("t", ["A"], [["x"], ["x"], ["y"]]);

        TableComparison.Compare(expected, new TableData("t", ["A"], [["y"], ["x"], ["x"]]), []).IsMatch.ShouldBeTrue();
        TableComparison.Compare(expected, new TableData("t", ["A"], [["y"], ["x"]]), []).Mismatches.ShouldBe(["missing row [A] = [x]"]);
    }

    [Fact]
    public void Canonical_values_never_round()
    {
        CanonicalValue.Decimal(12.5m, 2).ShouldBe("12.50");
        CanonicalValue.Decimal(-0.00m, 2).ShouldBe("0.00");
        CanonicalValue.Decimal(SqlDecimal.Parse("99999999999999999999999.99999999"), 8).ShouldBe("99999999999999999999999.99999999");
        CanonicalValue.Decimal(SqlDecimal.Parse("1.5"), 8).ShouldBe("1.50000000");
        Should.Throw<ArgumentOutOfRangeException>(() => CanonicalValue.Decimal(1.005m, 2));
        CanonicalValue.Timestamp(new DateTime(2026, 11, 4, 23, 59, 59).AddTicks(9_999_990), 6).ShouldBe("2026-11-04T23:59:59.999999");
        Should.Throw<ArgumentOutOfRangeException>(() => CanonicalValue.Timestamp(new DateTime(2026, 11, 4).AddTicks(1), 6));
        CanonicalValue.ParseTimestamp("2026-03-08T02:30:00.123456").Ticks.ShouldBe(new DateTime(2026, 3, 8, 2, 30, 0).AddTicks(1_234_560).Ticks);
        CanonicalValue.Char("BR", 3).ShouldBe("BR ");
    }

    [Theory]
    [InlineData("12.50", true)]
    [InlineData("12.5", false)]
    [InlineData("-0.00", false)]
    [InlineData("100000000000000.00", false)]
    public void Validation_accepts_only_canonical_text_that_fits_the_type(string text, bool valid)
    {
        (CanonicalValue.Validate(text, new ColumnDefinition("Amount", ColumnType.Decimal(15, 2))) is null).ShouldBe(valid);
    }
}
