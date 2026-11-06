using System.Text;
using CreditUnion.Batch.Core.Card;

namespace CreditUnion.Batch.UnitTests.Card;

/// <summary>The parsing rules of ADR 0010, taken from the dataset DS_Blob_CardSettlementCsv.</summary>
public sealed class SettlementCsvReaderTests
{
    private static async Task<List<string?[]>> ReadAsync(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = SettlementCsvReader.OpenText(stream);
        var rows = new List<string?[]>();
        await foreach (var row in SettlementCsvReader.ReadAsync(reader, "settlement_001.csv", TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static Task<List<string?[]>> ReadAsync(string text) => ReadAsync(Encoding.UTF8.GetBytes(text));

    private static string?[] Row(params string?[] fields) => [.. fields, .. new string?[SettlementCsvReader.ColumnCount - fields.Length]];

    [Fact]
    public async Task Records_are_split_on_commas_and_line_feeds_and_padded_to_ten_columns()
    {
        var rows = await ReadAsync("H,TESTPROC,20261104,0001\nD,REF1,20261103,400000,1234,25.00,SALE,CAFE,5411,USD\nT,1,25.00\n");

        rows.Count.ShouldBe(3);
        rows[0].ShouldBe(Row("H", "TESTPROC", "20261104", "0001"));
        rows[1].ShouldBe(["D", "REF1", "20261103", "400000", "1234", "25.00", "SALE", "CAFE", "5411", "USD"]);
        rows[2].ShouldBe(Row("T", "1", "25.00"));
    }

    [Fact]
    public async Task Quoted_fields_keep_commas_line_feeds_and_doubled_quotes()
    {
        var rows = await ReadAsync("D,R1,\"SYNTHETIC CAFE, INC\",\"SYNTH \"\"QUOTED\"\" BOOKS\",\"two\nlines\"\n");

        rows.Count.ShouldBe(1);
        rows[0].ShouldBe(Row("D", "R1", "SYNTHETIC CAFE, INC", "SYNTH \"QUOTED\" BOOKS", "two\nlines"));
    }

    [Fact]
    public async Task An_empty_value_is_null_whether_or_not_it_is_quoted_and_whitespace_is_kept()
    {
        var rows = await ReadAsync("D,R1,,\"\", ,x\n");

        rows[0].ShouldBe(Row("D", "R1", null, null, " ", "x"));
    }

    [Fact]
    public async Task A_byte_order_mark_is_not_part_of_the_first_field()
    {
        var rows = await ReadAsync([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("H,P,20261104,1\n")]);

        rows[0][0].ShouldBe("H");
    }

    [Fact]
    public async Task A_missing_final_line_feed_still_yields_the_last_record()
    {
        var rows = await ReadAsync("H,P,20261104,1\nT,0,0.00");

        rows.Count.ShouldBe(2);
        rows[1].ShouldBe(Row("T", "0", "0.00"));
    }

    [Fact]
    public async Task Empty_lines_are_skipped_including_trailing_ones()
    {
        var rows = await ReadAsync("\nH,P,20261104,1\n\n\nT,0,0.00\n\n\n");

        rows.Select(r => r[0]).ShouldBe(["H", "T"]);
    }

    [Fact]
    public async Task A_carriage_return_is_data_because_the_dataset_row_delimiter_is_a_line_feed()
    {
        var rows = await ReadAsync("D,R1,USD\r\nT,1,1.00\r\n");

        rows[0].ShouldBe(Row("D", "R1", "USD\r"));
        rows[1].ShouldBe(Row("T", "1", "1.00\r"));
    }

    [Fact]
    public async Task A_quote_in_the_middle_of_an_unquoted_field_and_text_after_a_closing_quote_are_data()
    {
        var rows = await ReadAsync("D,AB\"C,\"X\"Y,Z\n");

        rows[0].ShouldBe(Row("D", "AB\"C", "XY", "Z"));
    }

    [Fact]
    public async Task Fields_beyond_the_tenth_are_not_mapped()
    {
        var rows = await ReadAsync("D,1,2,3,4,5,6,7,8,9,10,11\n");

        rows[0].ShouldBe(["D", "1", "2", "3", "4", "5", "6", "7", "8", "9"]);
    }

    [Fact]
    public async Task Non_ASCII_text_is_decoded_as_UTF8()
    {
        var rows = await ReadAsync("D,R1,CAFÉ ☃\n");

        rows[0][2].ShouldBe("CAFÉ ☃");
    }

    [Fact]
    public async Task A_quoted_field_that_never_closes_is_a_data_error_that_does_not_quote_the_value()
    {
        var error = await Should.ThrowAsync<CardFileDataException>(() => ReadAsync("H,P,20261104,1\nD,R1,\"SECRET 4000001234567890\n"));

        error.Message.ShouldContain("settlement_001.csv");
        error.Message.ShouldContain("record 2");
        error.Message.ShouldNotContain("SECRET");
        error.Message.ShouldNotContain("4000001234567890");
    }

    [Fact]
    public async Task A_value_longer_than_the_staging_column_is_a_data_error_that_names_the_position_only()
    {
        var error = await Should.ThrowAsync<CardFileDataException>(() => ReadAsync("D,R1," + new string('9', SettlementCsvReader.MaxFieldLength + 1) + "\n"));

        error.Message.ShouldContain("record 1 field 3");
        error.Message.ShouldNotContain("999");
    }

    [Fact]
    public async Task A_value_exactly_as_long_as_the_staging_column_is_accepted()
    {
        var rows = await ReadAsync("D,R1," + new string('9', SettlementCsvReader.MaxFieldLength) + "\n");

        rows[0][2]!.Length.ShouldBe(SettlementCsvReader.MaxFieldLength);
    }

    [Fact]
    public async Task A_large_file_streams_without_holding_it_in_memory()
    {
        var text = new StringBuilder();
        for (var i = 0; i < 50_000; i++)
        {
            text.Append("D,REF").Append(i).Append(",20261103,400000,1234,1.00,SALE,M,5411,USD\n");
        }

        var rows = await ReadAsync(text.ToString());

        rows.Count.ShouldBe(50_000);
        rows[^1][1].ShouldBe("REF49999");
    }
}
