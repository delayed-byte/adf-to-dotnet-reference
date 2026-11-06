using System.Data.SqlTypes;
using CreditUnion.Batch.KeyStone.Staging;

namespace CreditUnion.Batch.UnitTests.Extract;

/// <summary>The database-free value rules of ADR 0005, one test per rule.</summary>
public sealed class StagingValueRulesTests
{
    private static StagingColumn Char(int length) => new("C", StagingKind.FixedChar, length, 0, 0);

    private static StagingColumn VarChar(int bytes) => new("V", StagingKind.VarChar, bytes, 0, 0);

    private static StagingColumn Decimal(int precision, int scale) => new("D", StagingKind.ExactDecimal, 0, precision, scale);

    [Theory]
    [InlineData("BR ")]
    [InlineData(" LN")]
    [InlineData("2100      ")]
    public void CHAR_values_keep_their_padding(string value)
    {
        StagingValueRules.Text(value, Char(value.Length)).ShouldBe(value);
    }

    [Fact]
    public void A_non_ASCII_CHAR_value_fails_the_copy_without_quoting_the_value()
    {
        var error = Should.Throw<StagingValueException>(() => StagingValueRules.Text("Zoë", Char(6)));

        error.Message.ShouldContain("non-ASCII");
        error.Message.ShouldNotContain("Zo");
    }

    [Fact]
    public void A_CHAR_value_longer_than_the_column_fails()
    {
        Should.Throw<StagingValueException>(() => StagingValueRules.Text("ABCD", Char(3)));
    }

    [Fact]
    public void VARCHAR_keeps_spaces_empty_strings_and_text_outside_Windows_1252()
    {
        StagingValueRules.Text("TRAILING   ", VarChar(60)).ShouldBe("TRAILING   ");
        StagingValueRules.Text(string.Empty, VarChar(60)).ShouldBe(string.Empty);
        StagingValueRules.Text("Café Zoë – Ω ✓ 日本", VarChar(60)).ShouldBe("Café Zoë – Ω ✓ 日本");
    }

    [Fact]
    public void VARCHAR_length_is_counted_in_bytes_as_Db2_counts_it()
    {
        StagingValueRules.Text("日本", VarChar(6)).ShouldBe("日本");
        Should.Throw<StagingValueException>(() => StagingValueRules.Text("日本", VarChar(5)));
    }

    [Theory]
    [InlineData("99999999999999999999999.99999999", 31, 8, "99999999999999999999999.99999999")]
    [InlineData("-99999999999999999999999.99999999", 31, 8, "-99999999999999999999999.99999999")]
    [InlineData("12345678901234567890123.45678901", 31, 8, "12345678901234567890123.45678901")]
    [InlineData("12.5", 15, 2, "12.50")]
    [InlineData("0", 15, 2, "0.00")]
    [InlineData("-0.01", 15, 2, "-0.01")]
    public void DECIMAL_values_are_exact_and_widened_to_the_column_scale(string text, int precision, int scale, string expected)
    {
        StagingValueRules.ExactDecimal(text, Decimal(precision, scale)).ToString().ShouldBe(expected);
    }

    [Fact]
    public void A_DECIMAL_with_more_fraction_digits_than_the_column_is_a_scale_mismatch_not_a_rounding()
    {
        var error = Should.Throw<StagingValueException>(() => StagingValueRules.ExactDecimal("12.345", Decimal(15, 2)));

        error.Message.ShouldContain("scale 3");
        error.Message.ShouldNotContain("12.345");
    }

    [Fact]
    public void A_DECIMAL_that_does_not_fit_the_column_fails()
    {
        Should.Throw<StagingValueException>(() => StagingValueRules.ExactDecimal("1234567890123456.00", Decimal(15, 2)));
    }

    [Fact]
    public void An_unreadable_DECIMAL_fails()
    {
        Should.Throw<StagingValueException>(() => StagingValueRules.ExactDecimal("not a number", Decimal(15, 2)));
    }

    [Fact]
    public void Money_is_never_read_as_double_so_a_31_digit_value_survives_as_SqlDecimal()
    {
        SqlDecimal value = StagingValueRules.ExactDecimal("99999999999999999999999.99999999", Decimal(31, 8));

        value.ToDouble().ShouldBe(1e23, "the double would be lossy; the SqlDecimal is not");
        value.ToString().ShouldBe("99999999999999999999999.99999999");
    }

    [Fact]
    public void DATE_is_midnight_with_no_time_zone_and_the_extremes_are_values()
    {
        StagingValueRules.Date(new DateTime(2026, 11, 4)).ShouldBe(new DateTime(2026, 11, 4));
        StagingValueRules.Date(DateTime.MinValue).ShouldBe(new DateTime(1, 1, 1));
        StagingValueRules.Date(new DateTime(9999, 12, 31)).ShouldBe(new DateTime(9999, 12, 31));
        Should.Throw<StagingValueException>(() => StagingValueRules.Date(new DateTime(2026, 11, 4, 0, 0, 1)));
    }

    [Fact]
    public void TIMESTAMP_is_copied_verbatim_including_a_time_in_the_spring_forward_gap_and_all_six_fraction_digits()
    {
        var gap = new DateTime(2026, 3, 8, 2, 30, 0).AddTicks(1_234_560);

        var copied = StagingValueRules.Timestamp(DateTime.SpecifyKind(gap, DateTimeKind.Local));

        copied.Ticks.ShouldBe(gap.Ticks);
        copied.Kind.ShouldBe(DateTimeKind.Unspecified, "no time zone conversion");
    }
}
