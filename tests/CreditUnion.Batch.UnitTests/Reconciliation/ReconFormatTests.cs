using System.Globalization;
using System.Text;
using System.Text.Json;
using CreditUnion.Batch.Core.Reconciliation;
using CreditUnion.Batch.TestData.Golden;
using CreditUnion.Batch.TestHarness.Golden;
using CreditUnion.Batch.TestHarness.Scenarios;

namespace CreditUnion.Batch.UnitTests.Reconciliation;

/// <summary>The notes formatter and the alert body builder are pure functions (ADR 0013). Money is decimal everywhere, printed with its SQL scale.</summary>
public sealed class ReconFormatTests
{
    private static readonly Guid RunId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly DateOnly Date = new(2026, 11, 4);

    [Theory]
    [InlineData("0.01", "0.01")]
    [InlineData("0.1", "0.10")]
    [InlineData("0", "0.00")]
    [InlineData("-0.01", "-0.01")]
    [InlineData("-12.5", "-12.50")]
    [InlineData("1234567.89", "1234567.89")]
    [InlineData("99999999999999999.99", "99999999999999999.99")]
    public void Money_keeps_the_SQL_scale_of_two_decimals_with_the_invariant_culture(string value, string expected)
    {
        var amount = decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            ReconFormat.Money(amount).ShouldBe(expected);
            ReconFormat.Notes(amount, 1, 0.5m).ShouldContain("difference=" + expected + ";");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Notes_print_the_card_difference_the_GL_item_count_and_the_tolerance_with_their_scale()
    {
        ReconFormat.Notes(0.00m, 0, 0.00m).ShouldBe("card difference=0.00; GL items out of tolerance=0; tolerance=0.00");
        ReconFormat.Notes(0.01m, 0, 0.00m).ShouldBe("card difference=0.01; GL items out of tolerance=0; tolerance=0.00");
        ReconFormat.Notes(-3.5m, 12, 0.25m).ShouldBe("card difference=-3.50; GL items out of tolerance=12; tolerance=0.25");
    }

    [Fact]
    public void The_body_for_the_golden_scenario_equals_the_golden_alert()
    {
        var scenario = Scenario.Load("recon-card-off-by-one-cent");
        var alert = new ReconAlert(Date, RunId, 0.01m, 0, 1, "CardSettlement ALL difference 0.01");

        var body = ReconFormat.AlertBody(alert);

        GoldenAssert.AlertDifferences(scenario, 1, [body], new RunTokens([RunId])).ShouldBeEmpty();
        body.ShouldBe("{\"type\":\"ReconciliationException\",\"businessDate\":\"2026-11-04\",\"batchRunId\":\"0f8fad5b-d9cb-469f-a165-70867728950e\",\"cardDifference\":0.01,\"glItemsOutOfTolerance\":0,\"exceptionCount\":1,\"summary\":\"CardSettlement ALL difference 0.01\"}");
    }

    [Theory]
    [InlineData("quote \" in the summary")]
    [InlineData("backslash \\ and \\\" mix")]
    [InlineData("line\nbreak\r\nand\ttab")]
    [InlineData("control \u0001 \u001f char")]
    [InlineData("brace } and { and ,\"glItemsOutOfTolerance\":99,")]
    [InlineData("non-ASCII é ü € 日本語 and 😀")]
    [InlineData("</script> & <b>")]
    [InlineData("")]
    public void The_summary_is_escaped_so_the_body_is_always_one_valid_JSON_document_that_carries_the_summary_unchanged(string summary)
    {
        var body = ReconFormat.AlertBody(new ReconAlert(Date, RunId, 0.01m, 1, 2, summary));

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("summary").GetString().ShouldBe(summary);
        root.EnumerateObject().Select(p => p.Name).ShouldBe(["type", "businessDate", "batchRunId", "cardDifference", "glItemsOutOfTolerance", "exceptionCount", "summary"]);
        root.GetProperty("glItemsOutOfTolerance").GetInt32().ShouldBe(1, "text in the summary cannot add or replace a property");
        body.ShouldNotContain("\n");
    }

    [Fact]
    public void An_unpaired_surrogate_which_a_truncated_summary_can_end_with_becomes_the_replacement_character()
    {
        var broken = "ends with half a pair \uD83D";

        var body = ReconFormat.AlertBody(new ReconAlert(Date, RunId, 0.01m, 0, 1, broken));

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("summary").GetString().ShouldBe("ends with half a pair �");
        Encoding.UTF8.GetBytes(body).Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void The_card_difference_is_a_JSON_number_equal_to_the_decimal_for_many_amounts()
    {
        var random = new Random(20261104);
        for (var i = 0; i < 500; i++)
        {
            var cents = random.NextInt64(-1_000_000_000_000, 1_000_000_000_000);
            var amount = new decimal(cents) / 100m;

            using var document = JsonDocument.Parse(ReconFormat.AlertBody(new ReconAlert(Date, RunId, amount, 0, 0, "s")));

            var number = document.RootElement.GetProperty("cardDifference");
            number.ValueKind.ShouldBe(JsonValueKind.Number);
            number.GetDecimal().ShouldBe(amount);
        }
    }

    [Theory]
    [InlineData("0.00")]
    [InlineData("0.01")]
    [InlineData("5")]
    [InlineData("99999999999999999.99")]
    public void Valid_tolerances_pass(string value)
    {
        Should.NotThrow(() => ReconFormat.ValidateTolerance(decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("0.005")]
    [InlineData("1.234")]
    [InlineData("100000000000000000.00")]
    public void A_tolerance_the_decimal_19_2_parameter_would_change_is_refused(string value)
    {
        Should.Throw<ReconInputException>(() => ReconFormat.ValidateTolerance(decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void No_alert_input_type_carries_a_URL_so_the_secret_cannot_cross_a_Durable_boundary()
    {
        foreach (var type in new[] { typeof(ReconAlert), typeof(ReconQuery), typeof(RecordReconExceptionsRequest), typeof(MarkReconAlertSentRequest), typeof(ReconExceptionResult), typeof(CardReconTotals), typeof(GlReconTotals) })
        {
            type.GetProperties().ShouldNotContain(p => p.PropertyType == typeof(Uri) || p.Name.Contains("Url", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase),
                $"{type.Name} is an activity input or output");
        }
    }
}
