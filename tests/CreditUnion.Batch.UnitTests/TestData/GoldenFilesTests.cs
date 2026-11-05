using System.Text.RegularExpressions;
using CreditUnion.Batch.TestData.Canonical;
using CreditUnion.Batch.TestData.Golden;
using CreditUnion.Batch.TestData.KeyStone;
using CreditUnion.Batch.TestData.Scenarios;
using CreditUnion.Batch.TestHarness.Scenarios;

namespace CreditUnion.Batch.UnitTests.TestData;

/// <summary>
/// The committed inputs and golden files are exactly what the seeded generator produces, are
/// internally consistent, and contain only synthetic, reserved-range data (PRD: no SSNs, test BIN).
/// </summary>
public sealed partial class GoldenFilesTests
{
    private static readonly string TestDataRoot = ScenarioFileSystem.TestDataRoot(RepoPaths.Root);
    private static readonly Lazy<GlMappingLoader> Mapping = new(() => new GlMappingLoader());

    public static TheoryData<string> CommittedScenarios => [.. Scenario.CommittedNames];

    [Theory]
    [MemberData(nameof(CommittedScenarios))]
    public void Committed_files_match_the_generator_byte_for_byte(string name)
    {
        var generated = ScenarioGenerator.Generate(ScenarioCatalog.Get(name), Mapping.Value.Contract);

        ScenarioFileSystem.Drift(generated, TestDataRoot).ShouldBeEmpty(
            "golden files are edited only through the generator; run ./build.ps1 golden and review the diff");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Performance_manifest_matches_the_generator()
    {
        var generated = ScenarioGenerator.Generate(ScenarioCatalog.Get("perf-extract-1m"), Mapping.Value.Contract);
        ScenarioFileSystem.Drift(generated, TestDataRoot).ShouldBeEmpty();
    }

    [Fact]
    public void Generation_is_deterministic()
    {
        var a = ScenarioGenerator.Generate(ScenarioCatalog.Get("recon-card-off-by-one-cent"), Mapping.Value.Contract);
        var b = ScenarioGenerator.Generate(ScenarioCatalog.Get("recon-card-off-by-one-cent"), Mapping.Value.Contract);

        a.Files.Keys.ShouldBe(b.Files.Keys);
        a.Files.Where(f => !f.Value.AsSpan().SequenceEqual(b.Files[f.Key])).Select(f => f.Key).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(CommittedScenarios))]
    public void Manifest_row_counts_and_checksums_match_the_committed_tables(string name)
    {
        var scenario = Scenario.Load(name);
        foreach (var table in scenario.Golden.Tables)
        {
            var data = scenario.GoldenTable(table.Name);
            data.Columns.ShouldBe(GoldenTables.Get(table.Name).ColumnNames, $"{name} {table.Name} columns");
            data.RowCount.ShouldBe(table.RowCount, $"{name} {table.Name} row count");
            TableChecksum.Compute(data).ShouldBe(table.Sha256, $"{name} {table.Name} checksum");
        }
    }

    [Fact]
    public void Standard_day_has_the_volumes_the_prd_specifies_for_AT_01()
    {
        var scenario = Scenario.Load("standard-day");

        scenario.GoldenTableExpectation("core.PostedTransaction").RowCount.ShouldBe(5_000);
        scenario.GoldenTableExpectation("core.Account").RowCount.ShouldBe(800);
        scenario.GoldenTableExpectation("core.GlEntry").RowCount.ShouldBe(300);
        scenario.Golden.Blobs.Count(b => b.Container == "card-archive").ShouldBe(3);
    }

    [Theory]
    [MemberData(nameof(CommittedScenarios))]
    public void Inputs_are_canonical_and_satisfy_the_db2_schema_constraints(string name)
    {
        var scenario = Scenario.Load(name);
        var problems = new List<string>();
        var tables = KeyStoneTables.LoadOrder.ToDictionary(t => t.Name, scenario.InputTable);
        foreach (var definition in KeyStoneTables.LoadOrder)
        {
            var data = tables[definition.Name];
            problems.AddRange(data.Rows.SelectMany((row, r) => definition.Columns.Select((c, i) => CanonicalValue.Validate(row[i], c)))
                .OfType<string>().Select(p => $"{definition.Name}: {p}"));
        }

        string[] baseCodes = ["DEP1", "ACHC", "WDL1", "ATMW", "ACHD", "CPOS", "CRFD", "LPMT", "DIVP", "FEE1"];
        string[] glAccounts = ["1010", "1020", "1110", "1300", "2100", "2400", "4100", "4300", "4310", "5100"];
        var codes = baseCodes.Concat(tables["KS.TXN_CODE"].Rows.Select(r => r[0]!)).ToHashSet(StringComparer.Ordinal);
        var members = tables["KS.MEMBER"].Rows.Select(r => r[0]).ToHashSet(StringComparer.Ordinal);
        var accounts = tables["KS.ACCOUNT"].Rows.Select(r => r[0]).ToHashSet(StringComparer.Ordinal);
        var txns = tables["KS.POSTED_TXN"];

        problems.AddRange(tables["KS.ACCOUNT"].Rows.Where(r => !members.Contains(r[1])).Select(r => $"account {r[0]} has no member"));
        problems.AddRange(tables["KS.ACCOUNT"].Rows.Where(r => r[11] is not ("O" or "D" or "C")).Select(r => $"account {r[0]} status"));
        problems.AddRange(txns.Rows.Where(r => !accounts.Contains(r[1])).Select(r => $"txn {r[0]} has no account"));
        problems.AddRange(txns.Rows.Where(r => !codes.Contains(r[2]!)).Select(r => $"txn {r[0]} code {r[2]}"));
        problems.AddRange(txns.Rows.Where(r => CanonicalValue.ParseDecimal(r[6]!) <= 0).Select(r => $"txn {r[0]} amount must be positive"));
        problems.AddRange(txns.Rows.Where(r => r[8] is not ("BR " or "ATM" or "ACH" or "CRD" or "WEB" or "SYS")).Select(r => $"txn {r[0]} channel '{r[8]}'"));
        problems.AddRange(tables["KS.GL_ENTRY"].Rows.Where(r => !glAccounts.Contains(r[1]!.TrimEnd())).Select(r => $"GL entry {r[0]} account {r[1]}"));
        problems.AddRange(tables["KS.GL_ENTRY"].Rows.Where(r => CanonicalValue.ParseDecimal(r[3]!) != 0 && CanonicalValue.ParseDecimal(r[4]!) != 0).Select(r => $"GL entry {r[0]} has both sides"));
        problems.AddRange(KeyStoneTables.LoadOrder.SelectMany(t => tables[t.Name].Rows.GroupBy(r => string.Join('|', t.KeyColumns.Select(k => r[tables[t.Name].ColumnIndex(k)])))
            .Where(g => g.Count() > 1).Select(g => $"{t.Name} duplicate key {g.Key}")));

        problems.ShouldBeEmpty();
    }

    [Fact]
    public void Synthetic_data_contains_no_ssn_like_values_no_full_card_numbers_and_only_reserved_identifiers()
    {
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(TestDataRoot, "inputs"), "*", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(Path.Combine(TestDataRoot, "golden"), "*", SearchOption.AllDirectories)))
        {
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(TestDataRoot, file);
            problems.AddRange(SsnPattern().Matches(text).Select(m => $"{relative}: SSN-like value {m.Value}"));
            problems.AddRange(CardNumberCandidates().Matches(text).Where(m => Luhn(m.Value)).Select(m => $"{relative}: Luhn-valid {m.Length}-digit number {m.Value[..6]}..."));
        }

        foreach (var name in Scenario.CommittedNames)
        {
            var scenario = Scenario.Load(name);
            problems.AddRange(scenario.InputTable(KeyStoneTables.Member).Rows.Where(r => !MemberNumber().IsMatch(r[0]!)).Select(r => $"{name}: member {r[0]} outside 9xxxxxxxxx"));
            problems.AddRange(scenario.InputTable(KeyStoneTables.Account).Rows.Where(r => !AccountNumber().IsMatch(r[0]!)).Select(r => $"{name}: account {r[0]} outside 99xxxxxxxxxx"));
            foreach (var blob in scenario.InputBlobs().Where(b => b.Name.EndsWith(".csv", StringComparison.Ordinal)))
            {
                var details = Rfc4180.Parse(CanonicalCsv.Encoding.GetString(blob.Content)).Where(r => r[0].Text == "D");
                problems.AddRange(details.Where(d => d[3].Text != "400000").Select(d => $"{name}: {blob.Name} uses BIN {d[3].Text}, not the test BIN"));
            }
        }

        problems.ShouldBeEmpty();
    }

    private static bool Luhn(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            sum += i % 2 == 1 ? (d * 2 > 9 ? (d * 2) - 9 : d * 2) : d;
        }

        return sum % 10 == 0;
    }

    [GeneratedRegex(@"(?<!\d)\d{3}-\d{2}-\d{4}(?!\d)")]
    private static partial Regex SsnPattern();

    // 13-19 digit runs that are not part of a longer identifier and not prefixed by letters (TXN_IDs and refs are excluded by the BIN check below).
    [GeneratedRegex(@"(?<![\dA-Za-z])4\d{12,18}(?![\d])")]
    private static partial Regex CardNumberCandidates();

    [GeneratedRegex(@"^9\d{9}$")]
    private static partial Regex MemberNumber();

    [GeneratedRegex(@"^99\d{10}$")]
    private static partial Regex AccountNumber();

    private sealed class GlMappingLoader
    {
        public CreditUnion.Batch.TestData.KeyBridge.GlMappingResponse Contract { get; } = ScenarioGenerator.LoadContractMapping(RepoPaths.Root);
    }
}
