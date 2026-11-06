using CreditUnion.Batch.Core.Card;

namespace CreditUnion.Batch.UnitTests.Card;

public sealed class SettlementFileFilterTests
{
    [Theory]
    [InlineData("settlement_001.csv", "File", true)]
    [InlineData("SETTLEMENT_003.CSV", "File", true)]
    [InlineData("Settlement_7.Csv", "File", true)]
    [InlineData("settlement_.csv", "File", true)]
    [InlineData("settlement_004.csv.tmp", "File", false)]
    [InlineData("readme.txt", "File", false)]
    [InlineData("old", "Folder", false)]
    [InlineData("settlement_old.csv", "Folder", false)]
    [InlineData("xsettlement_001.csv", "File", false)]
    [InlineData("settlement001.csv", "File", false)]
    [InlineData("settlement_001.csvx", "File", false)]
    [InlineData("settlement_001", "File", false)]
    public void A_child_item_is_a_settlement_file_when_it_is_a_file_that_starts_with_settlement_and_ends_with_csv_ignoring_case(string name, string type, bool expected) =>
        SettlementFileFilter.IsSettlementFile(new LandingItem(name, type)).ShouldBe(expected);

    [Fact]
    public void Select_keeps_the_matching_items_in_their_original_order()
    {
        var items = new LandingItem[]
        {
            new("settlement_002.csv", "File"), new("old", "Folder"), new("readme.txt", "File"), new("SETTLEMENT_001.CSV", "File"),
        };

        SettlementFileFilter.Select(items).Select(i => i.Name).ShouldBe(["settlement_002.csv", "SETTLEMENT_001.CSV"]);
    }

    [Fact]
    public void A_missing_child_list_is_an_empty_one() => SettlementFileFilter.Select(null).ShouldBeEmpty();

    [Theory]
    [InlineData(0, "No settlement files found in the landing folder.")]
    [InlineData(1, "1 settlement file(s) processed; see ops.CardFileLog.")]
    [InlineData(3, "3 settlement file(s) processed; see ops.CardFileLog.")]
    public void The_succeeded_note_is_built_as_the_ADF_expression_builds_it(int count, string expected) =>
        CardIngestNotes.ForFileCount(count).ShouldBe(expected);
}
