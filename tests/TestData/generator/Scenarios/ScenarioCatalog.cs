using System.Data.SqlTypes;
using System.Globalization;
using System.Text;
using CreditUnion.Batch.TestData.Card;
using CreditUnion.Batch.TestData.Golden;
using CreditUnion.Batch.TestData.KeyBridge;
using CreditUnion.Batch.TestData.KeyStone;
using CreditUnion.Batch.TestData.Oracle;
using CreditUnion.Batch.TestData.Reporting;
using CreditUnion.Batch.TestData.World;
using static CreditUnion.Batch.TestData.Canonical.CanonicalValue;

namespace CreditUnion.Batch.TestData.Scenarios;

/// <summary>
/// Every test scenario (PRD "Acceptance tests"). Acceptance tests that differ only in how the batch
/// is driven share a scenario: AT-04 (connection refused) and AT-11 (KeyBridge 503s) inject faults
/// into standard-day and must still produce its golden output.
/// </summary>
public static class ScenarioCatalog
{
    public static readonly DateOnly BusinessDate = new(2026, 11, 4);

    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    public static IReadOnlyList<ScenarioDefinition> All { get; } =
    [
        new("standard-day",
            "Seeded business day 2026-11-04: 5,000 transactions on 800 accounts, 300 GL entries and three valid settlement files, " +
            "with distractors (the previous day's data and files, non-matching names and a subfolder in the landing folder). Full nightly batch; everything reconciles.",
            ["AT-01", "AT-02", "AT-04", "AT-05", "AT-08", "AT-11", "AT-12", "AT-14", "AT-17"],
            20261104, [new RunSpec(1, Pipelines.Master, BusinessDate)], StandardDay),

        new("extract-db2-types",
            "Hand-written Db2 rows covering the type rules of ADR 0005: CHAR padding (trailing and leading), VARCHAR trailing spaces, empty string vs NULL, " +
            "NULL in every nullable column, DECIMAL(31,8) at full precision, DECIMAL(15,2) limits, TIMESTAMP(6) microseconds, DST-ambiguous and non-existent local times, " +
            "DATE limits, BIGINT limits, quotes, pipes, backslashes, line breaks and text outside Windows-1252.",
            ["AT-03"], 20261105, [new RunSpec(1, Pipelines.Extract, BusinessDate)], Db2TypeEdgeCases),

        new("card-quarantine-trailer-mismatch",
            "Three settlement files; the second claims one more detail record in its trailer than it contains. That file is quarantined with an error manifest; the other two load. " +
            "The third is named SETTLEMENT_003.CSV and is still picked up, because ADF's startsWith/endsWith ignore case (ADR 0007).",
            ["AT-06"], 20261106, [new RunSpec(1, Pipelines.Card, BusinessDate)], CardTrailerMismatch),

        new("card-empty-landing",
            "No settlement files for 2026-11-04 (the folder does not exist); a file for 2026-11-03 must be left alone. Card ingestion succeeds with zero rows and logs that no files were found.",
            ["AT-07"], 20261107, [new RunSpec(1, Pipelines.Card, BusinessDate)], CardEmptyLanding),

        new("map-unmapped-code",
            "KeyStone posted three transactions with a new code, P2PD, that the KeyBridge mapping does not cover. Map and Aggregate fails with TXN_CODE_UNMAPPED naming P2PD; " +
            "the master records the failure at the Map step and does not run Reconciliation.",
            ["AT-09", "AT-15"], 20261108, [new RunSpec(1, Pipelines.Master, BusinessDate)], UnmappedCode),

        new("map-midpoint-rounding",
            "Loan payments whose GL allocations (75% principal, 25% interest) land exactly on half a cent, e.g. 0.50 x 0.25 = 0.125. " +
            "Banker's rounding gives 0.12; rounding half away from zero would give 0.13. Includes 4.70 x 0.25 = 1.175, which rounds differently in binary floating point (ADR 0006).",
            ["AT-10"], 20261109, [new RunSpec(1, Pipelines.Master, BusinessDate)], MidpointRounding),

        new("recon-card-off-by-one-cent",
            "One settlement detail is 0.01 higher than the KeyStone card transaction it settles. The card check finds a difference of 0.01; " +
            "one exception row is written and exactly one alert is posted. The GL check reconciles.",
            ["AT-13"], 20261110, [new RunSpec(1, Pipelines.Master, BusinessDate)], CardOffByOneCent),

        new("rerun-earlier-date",
            "Nightly batch for 2026-11-03, then 2026-11-04, then 2026-11-03 again. The rerun changes nothing and touches only 2026-11-03.",
            ["AT-16"], 20261111,
            [new RunSpec(1, Pipelines.Master, BusinessDate.AddDays(-1)), new RunSpec(2, Pipelines.Master, BusinessDate), new RunSpec(3, Pipelines.Master, BusinessDate.AddDays(-1))],
            RerunEarlierDate),

        new("dst-transitions",
            "Trigger-driven runs on and after the 2026 spring-forward (2026-03-08) and fall-back (2026-11-01) days in America/Los_Angeles, " +
            "with transactions at the first and last microsecond of each day, in the repeated 01:00 hour, either side of the skipped 02:00 hour, " +
            "and one posted at 23:45 that KeyStone assigned to the next business date.",
            ["AT-18"], 20260308,
            [
                Triggered(1, new DateTimeOffset(2026, 3, 8, 10, 0, 0, TimeSpan.Zero)),
                Triggered(2, new DateTimeOffset(2026, 3, 9, 10, 0, 0, TimeSpan.Zero)),
                Triggered(3, new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.Zero)),
                Triggered(4, new DateTimeOffset(2026, 11, 2, 11, 0, 0, TimeSpan.Zero)),
            ],
            DstTransitions)
        {
            Schedule = [.. ScheduleWindow(new DateOnly(2026, 3, 6), 5), .. ScheduleWindow(new DateOnly(2026, 10, 30), 5)],
        },

        new("perf-extract-1m",
            "Performance (Category=Performance, excluded from the default run): 1,000,000 transactions on 50,000 accounts for one business date. " +
            "Only row counts and checksums are committed; inputs are regenerated with ./build.ps1 golden -Scenario perf-extract-1m.",
            ["NFR-Performance"], 20261199, [new RunSpec(1, Pipelines.Extract, BusinessDate)], Performance)
        {
            CommitInputs = false,
            CommitTableFiles = false,
            Category = "Performance",
        },
    ];

    public static ScenarioDefinition Get(string name) =>
        All.SingleOrDefault(s => s.Name == name) ?? throw new ArgumentException($"Unknown scenario '{name}'.", nameof(name));

    /// <summary>Business date for a trigger time: the previous calendar day in Pacific time (TR_Nightly_0300_Pacific).</summary>
    public static DateOnly BusinessDateFor(DateTimeOffset triggerTimeUtc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(triggerTimeUtc, Pacific).DateTime).AddDays(-1);

    private static RunSpec Triggered(int run, DateTimeOffset fireTimeUtc) => new(run, Pipelines.Master, BusinessDateFor(fireTimeUtc), fireTimeUtc);

    private static IEnumerable<ScheduleExpectation> ScheduleWindow(DateOnly first, int days) =>
        Enumerable.Range(0, days).Select(i =>
        {
            var local = first.AddDays(i).ToDateTime(new TimeOnly(3, 0));
            var offset = Pacific.GetUtcOffset(local);
            var utc = new DateTimeOffset(local, offset).ToUniversalTime();
            return new ScheduleExpectation(Date(first.AddDays(i)), new DateTimeOffset(local, offset).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), Date(BusinessDateFor(utc)));
        });

    private static ScenarioInputs StandardDay(ScenarioDefinition s, GlMappingResponse mapping)
    {
        var w = new WorldBuilder(s.Seed, mapping);
        w.AddAccountPool(shareAccounts: 900, loanAccounts: 120, asOf: BusinessDate);
        w.AddDay(new DaySpec(BusinessDate.AddDays(-1), Transactions: 200, ActiveAccounts: 50, GlEntries: 20, SettlementFiles: 1));
        w.AddDay(new DaySpec(BusinessDate, Transactions: 5_000, ActiveAccounts: 800, GlEntries: 300, SettlementFiles: 3));
        var world = w.Build();

        // Distractors in the landing container: only card-landing/2026-11-04/settlement_*.csv may be processed.
        var previousDayFile = world.SettlementFiles.Single(f => f.BusinessDate != BusinessDate);
        var distractors = new[]
        {
            Text(BatchOracle.LandingContainer, "2026-11-04/readme.txt", "Synthetic landing folder. Only settlement_*.csv files are processed.\n"),
            Text(BatchOracle.LandingContainer, "2026-11-04/settlement_004.csv.tmp", "partial upload\n"),
            new BlobItem(BatchOracle.LandingContainer, "2026-11-04/old/settlement_000.csv", previousDayFile.RenderBytes(), new Dictionary<string, string>()),
        };
        return new ScenarioInputs(world.KeyStone, world.SettlementFiles, distractors, mapping, KeyBridgeOverridden: false);
    }

    private static ScenarioInputs CardTrailerMismatch(ScenarioDefinition s, GlMappingResponse mapping)
    {
        var w = new WorldBuilder(s.Seed, mapping);
        w.AddAccountPool(60, 6, BusinessDate);
        w.AddDay(new DaySpec(BusinessDate, Transactions: 150, ActiveAccounts: 40, GlEntries: 12, SettlementFiles: 3));
        var world = w.Build();
        var files = world.SettlementFiles.ToList();
        files[1] = files[1] with { TrailerCount = (files[1].Details.Count + 1).ToString(CultureInfo.InvariantCulture) };
        files[2] = files[2] with { FileName = "SETTLEMENT_003.CSV" };
        return new ScenarioInputs(world.KeyStone, files, [], mapping, false);
    }

    private static ScenarioInputs CardEmptyLanding(ScenarioDefinition s, GlMappingResponse mapping)
    {
        var w = new WorldBuilder(s.Seed, mapping);
        w.AddAccountPool(10, 0, BusinessDate);
        w.AddDay(new DaySpec(BusinessDate.AddDays(-1), Transactions: 20, ActiveAccounts: 8, GlEntries: 0, SettlementFiles: 1, LoanAccountPercent: 0));
        var world = w.Build();
        return new ScenarioInputs(new KeyStoneDataset(), world.SettlementFiles, [], mapping, false);
    }

    private static ScenarioInputs UnmappedCode(ScenarioDefinition s, GlMappingResponse mapping)
    {
        // KeyStone posts P2PD with its own GL rules; KeyBridge does not know the code yet.
        var core = mapping with
        {
            Mappings = [.. mapping.Mappings, new GlMappingRow("P2PD", "2100", "DEBIT", 1.0m, null), new GlMappingRow("P2PD", "1110", "CREDIT", 1.0m, null)],
        };
        var w = new WorldBuilder(s.Seed, core);
        w.Extra.ExtraTxnCodes.Add(new TxnCodeRef("P2PD", "Person-to-person transfer (debit)", "WDL"));
        w.AddAccountPool(90, 10, BusinessDate);
        w.AddDay(new DaySpec(BusinessDate, Transactions: 200, ActiveAccounts: 60, GlEntries: 20, SettlementFiles: 1));
        w.AddTransaction(BusinessDate, At(BusinessDate, 9, 15, 0, 120_000), "P2PD", 25.00m, "P2P TRANSFER TO SYNTH FRIEND");
        w.AddTransaction(BusinessDate, At(BusinessDate, 13, 2, 41, 7), "P2PD", 140.50m, "P2P TRANSFER TO SYNTH FRIEND");
        w.AddTransaction(BusinessDate, At(BusinessDate, 21, 48, 9, 999_000), "P2PD", 9.99m, null);
        var world = w.Build();
        return new ScenarioInputs(world.KeyStone, world.SettlementFiles, [], mapping, false);
    }

    private static ScenarioInputs MidpointRounding(ScenarioDefinition s, GlMappingResponse mapping)
    {
        var map = mapping.WithSide("LPMT", "CREDIT", ("1300", 0.75m, "Loans to members - principal"), ("4100", 0.25m, "Loan interest income"));
        var w = new WorldBuilder(s.Seed, map);
        w.AddAccountPool(2, 1, BusinessDate);
        w.RegisterDay(new DaySpec(BusinessDate, 0, 0, GlEntries: 8, SettlementFiles: 0));
        decimal[] amounts = [0.50m, 0.10m, 0.10m, 0.10m, 0.30m, 0.70m, 1.10m, 4.70m, 2.70m, 10.10m, 1.00m, 0.33m];
        for (var i = 0; i < amounts.Length; i++)
        {
            w.AddTransaction(BusinessDate, At(BusinessDate, 8, 0, i, 0), "LPMT", amounts[i], "LOAN PAYMENT");
        }

        w.AddTransaction(BusinessDate, At(BusinessDate, 10, 30, 0, 0), "DEP1", 1_000.00m, "BRANCH DEPOSIT B001");
        var world = w.Build();
        var worksheet = new StringBuilder("## Rounding worksheet (review)\n\nEach loan payment is split 75% / 25%. Golden values use banker's rounding on the exact decimal product.\n\n")
            .Append("| Amount | x 0.75 exact | 1300 (to even) | half away from zero | x 0.25 exact | 4100 (to even) | half away from zero |\n| ---: | ---: | ---: | ---: | ---: | ---: | ---: |\n");
        foreach (var a in amounts)
        {
            worksheet.Append(CultureInfo.InvariantCulture, $"| {Decimal(a, 2)} | {a * 0.75m:0.0000} | {GlAllocation.Allocate(a, 0.75m):0.00} | {decimal.Round(a * 0.75m, 2, System.MidpointRounding.AwayFromZero):0.00} ")
                .Append(CultureInfo.InvariantCulture, $"| {a * 0.25m:0.0000} | {GlAllocation.Allocate(a, 0.25m):0.00} | {decimal.Round(a * 0.25m, 2, System.MidpointRounding.AwayFromZero):0.00} |\n");
        }

        worksheet.Append("\n4.70 x 0.25 = 1.175 exactly, so banker's rounding gives 1.18. As a binary double, 1.175 is 1.17499999999999982236431605997495353221893310546875 and rounds to 1.17. ")
            .Append("ADF's round() returns a double (ADR 0004); the golden value follows the PRD (decimal, MidpointRounding.ToEven). Confirm against a production ADF run.\n");
        return new ScenarioInputs(world.KeyStone, world.SettlementFiles, [], map, KeyBridgeOverridden: true) { ReviewNotes = worksheet.ToString() };
    }

    private static ScenarioInputs CardOffByOneCent(ScenarioDefinition s, GlMappingResponse mapping)
    {
        var w = new WorldBuilder(s.Seed, mapping);
        w.AddAccountPool(110, 12, BusinessDate);
        w.AddDay(new DaySpec(BusinessDate, Transactions: 300, ActiveAccounts: 80, GlEntries: 25, SettlementFiles: 2));
        var world = w.Build();
        var files = world.SettlementFiles.ToList();
        var file = files[0];
        var index = file.Details.ToList().FindIndex(d => d.TxnType == "SALE");
        var detail = file.Details[index];
        var amount = ParseDecimal(detail.Amount) + 0.01m;
        var details = file.Details.ToList();
        details[index] = detail with { Amount = Decimal(amount, 2) };
        files[0] = file with { Details = details, TrailerTotal = Decimal(ParseDecimal(file.TrailerTotal) + 0.01m, 2) };
        return new ScenarioInputs(world.KeyStone, files, [], mapping, false);
    }

    private static ScenarioInputs RerunEarlierDate(ScenarioDefinition s, GlMappingResponse mapping)
    {
        var w = new WorldBuilder(s.Seed, mapping);
        w.AddAccountPool(120, 12, BusinessDate);
        w.AddDay(new DaySpec(BusinessDate.AddDays(-1), Transactions: 150, ActiveAccounts: 50, GlEntries: 15, SettlementFiles: 1));
        w.AddDay(new DaySpec(BusinessDate, Transactions: 150, ActiveAccounts: 50, GlEntries: 15, SettlementFiles: 1));
        var world = w.Build();
        return new ScenarioInputs(world.KeyStone, world.SettlementFiles, [], mapping, false);
    }

    private static ScenarioInputs DstTransitions(ScenarioDefinition s, GlMappingResponse mapping)
    {
        var w = new WorldBuilder(s.Seed, mapping);
        w.AddAccountPool(80, 8, new DateOnly(2026, 3, 1));
        foreach (var (date, runDay) in new[] { (new DateOnly(2026, 3, 6), false), (new DateOnly(2026, 3, 7), true), (new DateOnly(2026, 3, 8), true), (new DateOnly(2026, 3, 9), false),
                                               (new DateOnly(2026, 10, 30), false), (new DateOnly(2026, 10, 31), true), (new DateOnly(2026, 11, 1), true), (new DateOnly(2026, 11, 2), false) })
        {
            w.AddDay(runDay ? new DaySpec(date, 40, 15, 6, 1) : new DaySpec(date, 10, 5, 0, 0));
        }

        void Edge(DateOnly posting, DateTime postedTs, string description) => w.AddTransaction(posting, postedTs, "DEP1", 10.00m, description);
        Edge(new(2026, 3, 7), At(new(2026, 3, 7), 0, 0, 0, 0), "EDGE first microsecond of 2026-03-07");
        Edge(new(2026, 3, 7), At(new(2026, 3, 7), 23, 59, 59, 999_999), "EDGE last microsecond of 2026-03-07");
        Edge(new(2026, 3, 8), At(new(2026, 3, 7), 23, 45, 0, 0), "EDGE posted 23:45 on 03-07, business date 03-08");
        Edge(new(2026, 3, 8), At(new(2026, 3, 8), 1, 59, 59, 999_999), "EDGE just before the spring-forward gap");
        Edge(new(2026, 3, 8), At(new(2026, 3, 8), 3, 0, 0, 0), "EDGE just after the spring-forward gap");
        Edge(new(2026, 3, 8), At(new(2026, 3, 8), 23, 59, 59, 999_999), "EDGE last microsecond of 2026-03-08");
        Edge(new(2026, 3, 9), At(new(2026, 3, 9), 0, 0, 0, 0), "EDGE first microsecond of 2026-03-09");
        Edge(new(2026, 10, 31), At(new(2026, 10, 31), 23, 59, 59, 999_999), "EDGE last microsecond of 2026-10-31");
        Edge(new(2026, 11, 1), At(new(2026, 11, 1), 0, 0, 0, 0), "EDGE first microsecond of 2026-11-01");
        Edge(new(2026, 11, 1), At(new(2026, 11, 1), 1, 30, 0, 0), "EDGE 01:30 first occurrence (PDT)");
        Edge(new(2026, 11, 1), At(new(2026, 11, 1), 1, 30, 0, 0), "EDGE 01:30 second occurrence (PST)");
        Edge(new(2026, 11, 1), At(new(2026, 11, 1), 23, 59, 59, 999_999), "EDGE last microsecond of 2026-11-01");
        Edge(new(2026, 11, 2), At(new(2026, 11, 2), 0, 0, 0, 1), "EDGE second microsecond of 2026-11-02");
        var world = w.Build();
        return new ScenarioInputs(world.KeyStone, world.SettlementFiles, [], mapping, false);
    }

    private static ScenarioInputs Performance(ScenarioDefinition s, GlMappingResponse mapping)
    {
        var w = new WorldBuilder(s.Seed, mapping);
        w.AddAccountPool(50_000, 5_500, BusinessDate);
        w.AddDay(new DaySpec(BusinessDate, Transactions: 1_000_000, ActiveAccounts: 50_000, GlEntries: 2_000, SettlementFiles: 0));
        var world = w.Build();
        return new ScenarioInputs(world.KeyStone, [], [], mapping, false);
    }

    // AT-03: each row is written out so a reviewer can see the edge case it covers (ADR 0005).
    private static ScenarioInputs Db2TypeEdgeCases(ScenarioDefinition s, GlMappingResponse mapping)
    {
        var d = BusinessDate;
        var data = new KeyStoneDataset();
        data.Members.Add(new Member("9000300001", "Avery", "Testcase", new DateOnly(2015, 6, 1), "B001", "A", At(new(2025, 1, 2), 9, 0, 0, 0)));
        data.Members.Add(new Member("9000300002", "Blake", "Sample", new DateOnly(2019, 2, 28), "B002", "A", At(new(2025, 3, 4), 10, 0, 0, 0)));

        SqlDecimal Exact(string text) => SqlDecimal.Parse(text);
        data.Accounts.AddRange(
        [
            // NULL in every nullable column; DECIMAL(31,8) maximum (GetDecimal silently rounds it); DECIMAL(15,2) and (9,6) maximums; CHAR(6) trailing padding.
            new Account("990000300001", "9000300001", "S", "SH01", null, new DateOnly(2015, 6, 1), null, 9_999_999_999_999.99m, null, 999.999999m,
                Exact("99999999999999999999999.99999999"), "O", d, At(d, 23, 59, 59, 999_999)),
            // Empty string (not NULL); DECIMAL minimums; CHAR(6) leading space; sentinel CLOSE_DATE 9999-12-31.
            new Account("990000300002", "9000300001", "L", " LN01", string.Empty, new DateOnly(2018, 1, 15), new DateOnly(9999, 12, 31), -9_999_999_999_999.99m, 0.00m, 0.000001m,
                Exact("-99999999999999999999999.99999999"), "O", d, At(d, 0, 0, 0, 0)),
            // VARCHAR trailing spaces; DATE minimum; smallest DECIMAL(31,8) step; LAST_MAINT_TS in the repeated 01:00 hour of 2026-11-01.
            new Account("990000300003", "9000300001", "S", "SH01", "TRAILING SPACES   ", new DateOnly(1, 1, 1), null, 0.01m, -0.01m, 0.000000m,
                Exact("0.00000001"), "O", d, At(new(2026, 11, 1), 1, 30, 0, 0)),
            // Text outside Windows-1252; 31 significant digits that GetDecimal rounds to ...123.456789; leap day; LAST_MAINT_TS in the skipped 02:00 hour of 2026-03-08.
            new Account("990000300004", "9000300002", "S", "SD0001", "Café Zoë – Ω ✓ 日本", new DateOnly(2024, 2, 29), null, 42.00m, 42.00m, 4.250000m,
                Exact("12345678901234567890123.45678901"), "O", d, At(new(2026, 3, 8), 2, 30, 0, 123_456)),
            // VARCHAR(60) filled to 60 ASCII characters; zero with scale.
            new Account("990000300005", "9000300002", "S", "SD0001", new string('X', 50) + "0123456789", new DateOnly(2020, 7, 1), null, 0.00m, 0.00m, 1.500000m,
                Exact("0.00000000"), "O", d, At(d, 12, 0, 0, 1)),
            // VARCHAR(60) filled to 60 bytes with 20 three-byte characters (fits Db2 OCTETS and SQL Server UTF-8 varchar(60)).
            new Account("990000300007", "9000300002", "S", "SH0100", "あいうえおかきくけこさしすせそたちつてと", new DateOnly(2021, 3, 3), null, 1.00m, 1.00m, 0.100000m,
                Exact("-0.00000001"), "O", d, At(d, 6, 30, 0, 500_000)),
            // Distractor: last activity the day before, so it is not extracted for 2026-11-04.
            new Account("990000300006", "9000300002", "S", "SH0001", "NOT EXTRACTED", new DateOnly(2020, 1, 1), null, 5.00m, 5.00m, 0.500000m,
                Exact("1.23456789"), "O", d.AddDays(-1), At(d.AddDays(-1), 17, 0, 0, 0)),
        ]);

        data.Transactions.AddRange(
        [
            // BIGINT maximum; CHAR(3) 'BR ' padding; NULLs; DECIMAL(15,2) maximum; last microsecond of the day.
            new PostedTxn(long.MaxValue, "990000300001", "DEP1", d, d, At(d, 23, 59, 59, 999_999), 9_999_999_999_999.99m, null, "BR ", null, null),
            // Smallest id and amount; quote, double quote, comma, pipe and backslash; reversal pointing at the BIGINT maximum.
            new PostedTxn(1, "990000300002", "WDL1", d, d.AddDays(-1), At(d, 0, 0, 0, 1), 0.01m, "O'Brien, \"quoted\" | pipe \\ backslash", "BR ", null, long.MaxValue),
            // Embedded line feed; CARD_PROCESSOR_REF at its 24-character limit.
            new PostedTxn(20261104_0000003, "990000300003", "CPOS", d, d, At(d, 12, 34, 56, 0), 12.30m, "LINE1\nLINE2", "CRD", "TP2026110400000000000099", null),
            // Leading and trailing spaces in VARCHAR; half-second timestamp.
            new PostedTxn(20261104_0000004, "990000300004", "DEP1", d, d, At(d, 8, 0, 0, 500_000), 100.00m, " LEADING AND TRAILING  ", "BR ", null, null),
            // Empty string description; ACH channel.
            new PostedTxn(20261104_0000005, "990000300005", "ACHD", d, d, At(d, 14, 15, 16, 171_819), 1.00m, string.Empty, "ACH", null, null),
            // Distractor: posted the day before.
            new PostedTxn(20261103_0000001, "990000300006", "DEP1", d.AddDays(-1), d.AddDays(-1), At(d.AddDays(-1), 17, 0, 0, 0), 5.00m, "NOT EXTRACTED", "BR ", null, null),
        ]);

        data.GlEntries.AddRange(
        [
            // CHAR(10) GL account and CHAR(4) source code padding; NULLs; credit at the DECIMAL(15,2) maximum.
            new GlEntry(2026110400001, "2100", d, 0.00m, 9_999_999_999_999.99m, "ACH", null, null, At(d, 23, 59, 59, 999_999)),
            // Empty BATCH_REF; MEMO at its 80-character limit.
            new GlEntry(2026110400002, "1010", d, 0.01m, 0.00m, "TELR", string.Empty, new string('M', 70) + "0123456789", At(d, 0, 0, 0, 0)),
            // MEMO outside Windows-1252; CREATED_TS in the skipped 02:00 hour.
            new GlEntry(2026110400003, "4310", d, 123.45m, 0.00m, "CARD", "GLB-20261104-001", "Memo with Ω and ✓", At(new(2026, 3, 8), 2, 30, 0, 0)),
            // Distractor: the day before.
            new GlEntry(2026110300001, "2100", d.AddDays(-1), 5.00m, 0.00m, "CORE", null, "NOT EXTRACTED", At(d.AddDays(-1), 17, 0, 0, 0)),
        ]);

        // Store CHAR values in their padded Db2 form, as the other scenarios do.
        Pad(data);
        return new ScenarioInputs(data, [], [], mapping, false);
    }

    private static void Pad(KeyStoneDataset data)
    {
        for (var i = 0; i < data.Accounts.Count; i++)
        {
            data.Accounts[i] = data.Accounts[i] with { ProductCode = Char(data.Accounts[i].ProductCode, 6) };
        }

        for (var i = 0; i < data.GlEntries.Count; i++)
        {
            data.GlEntries[i] = data.GlEntries[i] with { GlAccountNbr = Char(data.GlEntries[i].GlAccountNbr, 10), SourceCode = Char(data.GlEntries[i].SourceCode, 4) };
        }
    }

    private static DateTime At(DateOnly date, int hour, int minute, int second, int microsecond) =>
        date.ToDateTime(new TimeOnly(hour, minute, second)).AddTicks(microsecond * 10L);

    private static BlobItem Text(string container, string name, string content) =>
        new(container, name, Canonical.CanonicalCsv.Encoding.GetBytes(content), new Dictionary<string, string>());
}
