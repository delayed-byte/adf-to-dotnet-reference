using CreditUnion.Batch.Core.Extract;
using CreditUnion.Batch.Core.Logging;

namespace CreditUnion.Batch.UnitTests.Extract;

public sealed class ExtractQueryBuilderTests
{
    private static readonly ExtractTableConfig Transactions = new(
        "Transactions", "KS", "POSTED_TXN",
        "TXN_ID, ACCOUNT_NBR, TXN_CODE, POSTING_DATE", "TXN_ID", "POSTING_DATE", "stg", "KsPostedTxn");

    [Fact]
    public void The_select_is_the_ADF_query_with_the_business_date_as_a_Db2_DATE_literal()
    {
        ExtractQueryBuilder.BuildSelect(Transactions, new DateOnly(2026, 11, 4))
            .ShouldBe("SELECT TXN_ID, ACCOUNT_NBR, TXN_CODE, POSTING_DATE FROM KS.POSTED_TXN WHERE POSTING_DATE = DATE('2026-11-04')");
    }

    [Fact]
    public void The_truncate_is_the_ADF_pre_copy_script_bracket_quoted()
    {
        ExtractQueryBuilder.BuildTruncate(Transactions).ShouldBe("TRUNCATE TABLE [stg].[KsPostedTxn]");
    }

    [Fact]
    public void Key_columns_are_returned_in_configuration_order()
    {
        ExtractQueryBuilder.KeyColumns(Transactions with { KeyColumns = "A, B" }).ShouldBe(["A", "B"]);
    }

    [Theory]
    [InlineData("TXN_ID; DROP TABLE KS.POSTED_TXN")]
    [InlineData("TXN_ID, (SELECT 1)")]
    [InlineData("TXN ID")]
    [InlineData("1TXN")]
    [InlineData("")]
    public void A_source_column_that_is_not_a_plain_identifier_is_a_configuration_error(string columns)
    {
        Should.Throw<ExtractConfigurationException>(() => ExtractQueryBuilder.BuildSelect(Transactions with { SourceColumns = columns }, new DateOnly(2026, 11, 4)));
    }

    [Theory]
    [InlineData("KS; --")]
    [InlineData("KS.X")]
    [InlineData("")]
    public void Schema_table_and_date_column_names_are_checked_too(string value)
    {
        Should.Throw<ExtractConfigurationException>(() => ExtractQueryBuilder.BuildSelect(Transactions with { SourceSchema = value }, default));
        Should.Throw<ExtractConfigurationException>(() => ExtractQueryBuilder.BuildSelect(Transactions with { SourceTable = value }, default));
        Should.Throw<ExtractConfigurationException>(() => ExtractQueryBuilder.BuildSelect(Transactions with { DateColumn = value }, default));
        Should.Throw<ExtractConfigurationException>(() => ExtractQueryBuilder.BuildTruncate(Transactions with { TargetSchema = value }));
        Should.Throw<ExtractConfigurationException>(() => ExtractQueryBuilder.BuildTruncate(Transactions with { TargetTable = value }));
    }

    [Fact]
    public void The_configuration_error_names_the_row_not_the_offending_text()
    {
        var error = Should.Throw<ExtractConfigurationException>(() =>
            ExtractQueryBuilder.BuildSelect(Transactions with { SourceColumns = "TXN_ID; DROP TABLE X" }, default));

        error.Message.ShouldContain("Transactions");
        error.Message.ShouldNotContain("DROP");
    }

    [Theory]
    [InlineData("990000100001", "****0001")]
    [InlineData("1234", "****")]
    [InlineData("", "****")]
    [InlineData(null, "****")]
    [InlineData("12345", "****2345")]
    public void Identifiers_are_masked_to_their_last_four_characters(string? value, string expected)
    {
        LogMask.LastFour(value).ShouldBe(expected);
    }
}
