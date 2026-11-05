using System.Data.SqlTypes;
using System.Globalization;
using CreditUnion.Batch.TestData.Card;
using CreditUnion.Batch.TestData.KeyBridge;
using CreditUnion.Batch.TestData.KeyStone;
using CreditUnion.Batch.TestData.Oracle;
using CreditUnion.Batch.TestData.Reporting;
using static CreditUnion.Batch.TestData.Canonical.CanonicalValue;

namespace CreditUnion.Batch.TestData.World;

/// <summary>
/// Shape of one synthetic business day. <see cref="ActiveAccounts"/> accounts get at least one
/// transaction (so exactly that many accounts have this date as LAST_ACTIVITY_DATE unless they also
/// have later activity); GL entries and settlement files are derived from the day's transactions.
/// </summary>
public sealed record DaySpec(DateOnly Date, int Transactions, int ActiveAccounts, int GlEntries, int SettlementFiles, int LoanAccountPercent = 10);

public sealed record SyntheticWorld(KeyStoneDataset KeyStone, IReadOnlyList<SettlementFile> SettlementFiles);

/// <summary>
/// Builds a consistent synthetic KeyStone world from a seed (ADR 0006): the GL entries KeyStone
/// posts for a day equal, per GL account and side, what the mapping derives from that day's
/// transactions, and the processor's settlement files contain exactly the day's card transactions.
/// A scenario then introduces the one inconsistency it is about (an off-by-a-cent settlement row, a
/// wrong trailer count, an unmapped code). Only synthetic, reserved-range identifiers are produced:
/// member numbers 9xxxxxxxxx, account numbers 99xxxxxxxxxx, card BIN 400000 with last four digits
/// only, and no SSN or tax ID of any kind.
/// </summary>
public sealed class WorldBuilder
{
    public const string TestBin = "400000";
    public const string ProcessorId = "TESTPROC";

    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
    private static readonly string[] FirstNames = ["Avery", "Blake", "Casey", "Devon", "Emerson", "Finley", "Harper", "Jordan", "Kendall", "Logan", "Morgan", "Parker", "Quinn", "Reese", "Riley", "Rowan", "Sage", "Skyler", "Taylor", "Zion"];
    private static readonly string[] LastNames = ["Testcase", "Sample", "Example", "Fixture", "Mockridge", "Stubbs", "Dummond", "Placeholder", "Synthwood", "Dataford"];

    // Merchant names exercise CSV quoting (comma, double quote), SQL quoting (apostrophe) and text
    // outside Windows-1252 (Ł, Ź), which only survives because of migration 0007 (ADR 0005).
    private static readonly (string Name, string Mcc)[] Merchants =
    [
        ("SYNTHETIC GROCER", "5411"), ("SYNTH FUEL STOP", "5541"), ("SYNTHETIC CAFE, INC", "5814"), ("O'MALLEY'S SYNTH PUB", "5813"),
        ("CAFÉ ZOË SYNTHÉTIQUE", "5812"), ("ŁÓDŹ SYNTH DELI", "5499"), ("SYNTH \"QUOTED\" BOOKS", "5942"), ("SYNTH PHARMACY", "5912"),
        ("SYNTH HARDWARE", "5251"), ("SYNTH ONLINE MARKET", ""),
    ];

    private static readonly (string Code, int Weight)[] ShareMix =
        [("CPOS", 35), ("CRFD", 2), ("DEP1", 8), ("ACHC", 12), ("WDL1", 5), ("ATMW", 8), ("ACHD", 15), ("DIVP", 4), ("FEE1", 5)];

    private readonly DeterministicRandom rng;
    private readonly IReadOnlyList<TxnCodeGlMapRow> coreMapping;
    private readonly List<Member> members = [];
    private readonly List<AccountState> accounts = [];
    private readonly List<PendingTxn> pending = [];
    private readonly Dictionary<DateOnly, DaySpec> days = [];
    private readonly Dictionary<DateOnly, List<AccountState>> activeByDay = [];
    private readonly Dictionary<DateOnly, PendingTxn> lastPurchase = [];
    private long nextMember = 9_000_000_000;
    private long nextAccount = 990_000_000_000;

    /// <param name="seed">Scenario seed.</param>
    /// <param name="coreMapping">The GL mapping KeyStone itself posts with (the KeyBridge mapping plus any code KeyBridge does not know yet).</param>
    public WorldBuilder(ulong seed, GlMappingResponse coreMapping)
    {
        ArgumentNullException.ThrowIfNull(coreMapping);
        rng = new DeterministicRandom(seed);
        this.coreMapping = [.. coreMapping.Mappings.Select(m => new TxnCodeGlMapRow(Char(m.TransactionCode, 4), Char(m.GlAccountNumber, 10),
            m.Side == "DEBIT" ? "D" : "C", m.AllocationRatio, m.Description, coreMapping.Version))];
    }

    public KeyStoneDataset Extra { get; } = new();

    /// <summary>Accounts with no activity yet; their LAST_ACTIVITY_DATE stays in the past (dormant) unless a day uses them.</summary>
    public void AddAccountPool(int shareAccounts, int loanAccounts, DateOnly asOf)
    {
        var r = rng.Fork($"pool/{accounts.Count}");
        var toCreate = Enumerable.Repeat("S", shareAccounts).Concat(Enumerable.Repeat("L", loanAccounts)).ToList();
        toCreate = r.Shuffle(toCreate);
        var i = 0;
        while (i < toCreate.Count)
        {
            var member = NewMember(r, asOf);
            var perMember = Math.Min(r.Next(1, 4), toCreate.Count - i);
            for (var k = 0; k < perMember; k++, i++)
            {
                accounts.Add(NewAccount(r, member, toCreate[i], asOf));
            }
        }
    }

    public IReadOnlyList<string> AccountNumbers(string category) => [.. accounts.Where(a => a.Category == category).Select(a => a.AccountNbr)];

    /// <summary>Generates a day of random transactions over <see cref="DaySpec.ActiveAccounts"/> accounts.</summary>
    public void AddDay(DaySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        days[spec.Date] = spec;
        var r = rng.Fork($"day/{Date(spec.Date)}");
        var loans = (int)Math.Round(spec.ActiveAccounts * spec.LoanAccountPercent / 100m, MidpointRounding.AwayFromZero);
        var active = r.Shuffle(accounts.Where(a => a.Category == "L")).Take(loans)
            .Concat(r.Shuffle(accounts.Where(a => a.Category == "S")).Take(spec.ActiveAccounts - loans)).ToList();
        if (active.Count != spec.ActiveAccounts)
        {
            throw new InvalidOperationException($"Account pool too small for {spec.ActiveAccounts} active accounts on {Date(spec.Date)}.");
        }

        activeByDay[spec.Date] = active;
        var shares = active.Where(a => a.Category == "S").ToList();
        var loanAccounts = active.Where(a => a.Category == "L").ToList();
        foreach (var account in active.Take(spec.Transactions))
        {
            AddRandomTxn(r, spec.Date, account, account.Category == "L" ? "LPMT" : r.PickWeighted(ShareMix));
        }

        for (var n = active.Count; n < spec.Transactions; n++)
        {
            var useLoan = loanAccounts.Count > 0 && r.Chance(6);
            AddRandomTxn(r, spec.Date, useLoan ? r.Pick(loanAccounts) : r.Pick(shares), useLoan ? "LPMT" : r.PickWeighted(ShareMix));
        }
    }

    /// <summary>Adds a specific transaction (edge cases: near midnight, DST, rounding midpoints).</summary>
    public void AddTransaction(DateOnly postingDate, DateTime postedTs, string code, decimal amount, string? description, string? accountNbr = null)
    {
        var category = code == "LPMT" ? "L" : "S";
        var candidates = (activeByDay.TryGetValue(postingDate, out var active) ? active : accounts).Where(x => x.Category == category).ToList();
        var account = accountNbr is null
            ? rng.Fork($"explicit/{pending.Count}").Pick(candidates.Count > 0 ? candidates : accounts.Where(x => x.Category == category).ToList())
            : accounts.Single(x => x.AccountNbr == accountNbr);
        pending.Add(new PendingTxn(account, code, postingDate, postingDate, postedTs, amount, description, ChannelFor(code)));
    }

    /// <summary>Registers a day that has only explicit transactions, so its GL entries and settlement files are generated.</summary>
    public void RegisterDay(DaySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        days[spec.Date] = spec;
    }

    public SyntheticWorld Build()
    {
        var data = new KeyStoneDataset();
        data.ExtraTxnCodes.AddRange(Extra.ExtraTxnCodes);
        var files = new List<SettlementFile>();

        foreach (var group in pending.GroupBy(p => p.PostingDate).OrderBy(g => g.Key))
        {
            var seq = 0;
            var cardSeq = 0;
            // OrderBy is stable, so transactions with the same timestamp keep their insertion order.
            foreach (var p in group.OrderBy(p => p.PostedTs))
            {
                p.TxnId = (long.Parse(group.Key.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) * 10_000_000) + ++seq;
                if (p.Code is "CPOS" or "CRFD")
                {
                    p.CardRef = string.Create(CultureInfo.InvariantCulture, $"TP{group.Key:yyyyMMdd}{++cardSeq:D12}");
                }
            }
        }

        foreach (var p in pending.Where(p => p.ReversalOf is not null && p.ReversalOf.TxnId == 0))
        {
            p.ReversalOf = null;
        }

        var txns = pending.OrderBy(p => p.TxnId).Select(p => new PostedTxn(p.TxnId, p.Account.AccountNbr, p.Code, p.PostingDate, p.EffectiveDate, p.PostedTs,
            p.Amount, p.Description, p.Channel, p.CardRef, p.ReversalOf?.TxnId)).ToList();
        data.Transactions.AddRange(txns);
        data.Members.AddRange(members);
        var byAccount = pending.ToLookup(p => p.Account.AccountNbr, StringComparer.Ordinal);
        data.Accounts.AddRange(accounts.Select(a => a.ToAccount(byAccount[a.AccountNbr])));

        foreach (var spec in days.Values.OrderBy(d => d.Date))
        {
            var r = rng.Fork($"derive/{Date(spec.Date)}");
            data.GlEntries.AddRange(GlEntriesFor(r, spec, txns));
            files.AddRange(SettlementFilesFor(r, spec));
        }

        return new SyntheticWorld(data, files);
    }

    private void AddRandomTxn(DeterministicRandom r, DateOnly date, AccountState account, string code)
    {
        var cents = code switch
        {
            "CPOS" => r.Chance(70) ? r.Next(100, 6_000) : r.Next(6_000, 25_000),
            "CRFD" => r.Next(500, 10_000),
            "DEP1" => r.Next(2_000, 200_000),
            "ACHC" => r.Next(10_000, 400_000),
            "WDL1" => r.Next(2_000, 100_000),
            "ATMW" => r.Next(1, 21) * 2_000,
            "ACHD" => r.Next(1_000, 150_000),
            "LPMT" => r.Next(5_000, 90_000),
            "DIVP" => r.Next(1, 5_000),
            "FEE1" => r.Pick([500, 1_000, 1_500, 2_500, 3_500]),
            _ => throw new ArgumentException($"No amount rule for {code}.", nameof(code)),
        };

        var merchant = r.Pick(Merchants);
        var description = r.Chance(5) ? null : code switch
        {
            "CPOS" => $"POS {merchant.Name} {r.Next(1, 999):D3}",
            "CRFD" => $"REFUND {merchant.Name}",
            "DEP1" => $"BRANCH DEPOSIT B{r.Next(1, 9):D3}",
            "ACHC" => "ACH CREDIT " + r.Pick(["SYNTH CORP PAYROLL", "EXAMPLE MFG PAYROLL", "TEST LOGISTICS PAY"]),
            "WDL1" => "BRANCH WITHDRAWAL",
            "ATMW" => $"ATM WITHDRAWAL T{r.Next(1, 60):D4}",
            "ACHD" => "ACH DEBIT " + r.Pick(["SYNTH POWER & LIGHT", "EXAMPLE MORTGAGE CO", "TEST WIRELESS"]),
            "LPMT" => "LOAN PAYMENT",
            "DIVP" => "DIVIDEND",
            "FEE1" => "MONTHLY SERVICE FEE",
            _ => null,
        };

        var effective = code is "CPOS" or "CRFD" && r.Chance(40) ? date.AddDays(-1) : date;
        var txn = new PendingTxn(account, code, date, effective, RandomLocalTime(r, date), cents / 100m, description, ChannelFor(code))
        {
            Merchant = merchant,
        };
        if (code == "CRFD" && r.Chance(50))
        {
            txn.ReversalOf = lastPurchase.GetValueOrDefault(date);
        }
        else if (code == "CPOS")
        {
            lastPurchase[date] = txn;
        }

        pending.Add(txn);
    }

    private IEnumerable<GlEntry> GlEntriesFor(DeterministicRandom r, DaySpec spec, List<PostedTxn> txns)
    {
        if (spec.GlEntries == 0)
        {
            yield break;
        }

        var totals = GlAllocation.DailyTotals(spec.Date, txns, coreMapping);
        var pairs = totals.SelectMany(t => new[] { (t.GlAccountNbr, Debit: true, Cents: (long)(t.DebitTotal * 100)), (t.GlAccountNbr, Debit: false, Cents: (long)(t.CreditTotal * 100)) })
            .Where(p => p.Cents > 0).ToList();
        if (pairs.Count == 0)
        {
            yield break;
        }

        var target = Math.Max(spec.GlEntries, pairs.Count);
        var sum = pairs.Sum(p => p.Cents);
        // Spread the entries over the (account, side) pairs in proportion to their amounts, at least
        // one entry per pair and never more entries than cents.
        var counts = pairs.Select(p => (int)Math.Max(1, Math.Min(p.Cents, p.Cents * (target - pairs.Count) / sum + 1))).ToArray();
        while (counts.Sum() < target && Enumerable.Range(0, pairs.Count).Any(i => counts[i] < pairs[i].Cents))
        {
            var grow = Enumerable.Range(0, pairs.Count).Where(i => counts[i] < pairs[i].Cents).MaxBy(i => pairs[i].Cents / (decimal)counts[i]);
            counts[grow]++;
        }

        while (counts.Sum() > target && counts.Any(c => c > 1))
        {
            counts[Array.IndexOf(counts, counts.Max())]--;
        }

        var seq = 0;
        var batch = 1;
        var day = long.Parse(spec.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        for (var i = 0; i < pairs.Count; i++)
        {
            foreach (var cents in Split(r, pairs[i].Cents, counts[i]))
            {
                var amount = cents / 100m;
                var source = pairs[i].GlAccountNbr.TrimEnd() switch { "1010" => "TELR", "1020" => "ATM", "1110" => "ACH", "2400" => "CARD", _ => "CORE" };
                if (r.Chance(10))
                {
                    batch++;
                }

                yield return new GlEntry((day * 100_000) + ++seq, pairs[i].GlAccountNbr, spec.Date, pairs[i].Debit ? amount : 0m, pairs[i].Debit ? 0m : amount,
                    Char(source, 4), r.Chance(15) ? null : string.Create(CultureInfo.InvariantCulture, $"GLB-{spec.Date:yyyyMMdd}-{batch:D3}"),
                    r.Chance(30) ? null : string.Create(CultureInfo.InvariantCulture, $"Daily posting batch {batch:D3}"), RandomLocalTime(r, spec.Date));
            }
        }
    }

    private IEnumerable<SettlementFile> SettlementFilesFor(DeterministicRandom r, DaySpec spec)
    {
        if (spec.SettlementFiles == 0)
        {
            yield break;
        }

        var card = pending.Where(p => p.PostingDate == spec.Date && p.CardRef is not null).OrderBy(p => p.TxnId).ToList();
        for (var f = 0; f < spec.SettlementFiles; f++)
        {
            var slice = card.Skip(card.Count * f / spec.SettlementFiles).Take((card.Count * (f + 1) / spec.SettlementFiles) - (card.Count * f / spec.SettlementFiles)).ToList();
            var details = slice.Select(p =>
            {
                var merchant = p.Merchant ?? Merchants[0];
                return new SettlementDetail(p.CardRef!, p.EffectiveDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture), TestBin, p.Account.CardLast4,
                    AmountText(r, p.Amount), p.Code == "CPOS" ? "SALE" : "RFND", r.Chance(3) ? string.Empty : merchant.Name,
                    r.Chance(5) ? string.Empty : merchant.Mcc, "USD");
            }).ToList();
            yield return new SettlementFile(spec.Date, $"settlement_{f + 1:D3}.csv", ProcessorId, spec.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                (f + 1).ToString("D4", CultureInfo.InvariantCulture), details, details.Count.ToString(CultureInfo.InvariantCulture),
                Decimal(slice.Sum(p => p.Amount), 2));
        }
    }

    // The contract allows 1-2 decimals or none, so some amounts are written "12.5" or "12".
    private static string AmountText(DeterministicRandom r, decimal amount)
    {
        var text = Decimal(amount, 2);
        if (text.EndsWith("00", StringComparison.Ordinal) && r.Chance(50))
        {
            return text[..^3];
        }

        return text.EndsWith('0') && r.Chance(50) ? text[..^1] : text;
    }

    private static IEnumerable<long> Split(DeterministicRandom r, long total, int parts)
    {
        var cuts = new SortedSet<long>();
        while (cuts.Count < parts - 1)
        {
            cuts.Add(r.NextInt64(1, total));
        }

        var previous = 0L;
        foreach (var cut in cuts.Append(total))
        {
            yield return cut - previous;
            previous = cut;
        }
    }

    /// <summary>A KeyStone local timestamp on <paramref name="date"/> with microsecond precision; skips the spring-forward gap.</summary>
    private static DateTime RandomLocalTime(DeterministicRandom r, DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue).AddTicks(r.NextInt64(0, 86_400_000_000) * 10);
        return Pacific.IsInvalidTime(local) ? local.AddHours(1) : local;
    }

    private static string ChannelFor(string code) => code switch
    {
        "CPOS" or "CRFD" => "CRD",
        "DEP1" or "WDL1" => "BR ",
        "ATMW" => "ATM",
        "ACHC" or "ACHD" or "P2PD" => "ACH",
        "LPMT" => "WEB",
        _ => "SYS",
    };

    private Member NewMember(DeterministicRandom r, DateOnly asOf)
    {
        nextMember += r.Next(1, 9);
        var since = new DateOnly(2000, 1, 1).AddDays(r.Next(0, asOf.DayNumber - new DateOnly(2000, 1, 1).DayNumber - 180));
        var member = new Member(nextMember.ToString(CultureInfo.InvariantCulture), r.Pick(FirstNames), r.Pick(LastNames), since,
            "B" + r.Next(1, 9).ToString("D3", CultureInfo.InvariantCulture), "A", since.ToDateTime(new TimeOnly(9, 0)).AddTicks(r.Next(0, 28_800) * TimeSpan.TicksPerSecond));
        members.Add(member);
        return member;
    }

    private AccountState NewAccount(DeterministicRandom r, Member member, string category, DateOnly asOf)
    {
        nextAccount += r.Next(1, 9);
        var open = member.MemberSince.AddDays(r.Next(0, Math.Max(1, asOf.DayNumber - member.MemberSince.DayNumber - 90)));
        var (product, description) = category == "S"
            ? r.Pick([("SH0001", "Regular Share"), ("SD0001", "Share Draft Checking"), ("SH0100", "Holiday Club")])
            : r.Pick([("LN0100", "New Auto Loan"), ("LN0200", "Personal Loan"), ("LN0300", "Used Auto Loan")]);
        var balance = category == "S" ? r.Next(0, 5_000_000) / 100m : r.Next(100_000, 4_000_000) / 100m;
        var dormantSince = asOf.AddDays(-r.Next(30, 400));
        return new AccountState(nextAccount.ToString(CultureInfo.InvariantCulture), member.MemberNbr, category, product,
            r.Chance(10) ? null : description, open < dormantSince ? open : dormantSince.AddDays(-1), balance,
            category == "S" ? (r.Chance(10) ? null : balance - (r.Next(0, 20_000) / 100m)) : null,
            category == "S" ? r.Next(50_000, 4_500_000) / 1_000_000m : r.Next(2_990_000, 18_000_000) / 1_000_000m,
            SqlDecimal.ConvertToPrecScale(new SqlDecimal(r.NextInt64(0, 100_000_000_000) / 100_000_000m), 31, 8), dormantSince,
            dormantSince.ToDateTime(new TimeOnly(18, 0)), r.Next(0, 10_000).ToString("D4", CultureInfo.InvariantCulture));
    }

    private sealed record AccountState(
        string AccountNbr,
        string MemberNbr,
        string Category,
        string ProductCode,
        string? Description,
        DateOnly OpenDate,
        decimal CurrentBalance,
        decimal? AvailableBalance,
        decimal InterestRate,
        SqlDecimal AccruedInterest,
        DateOnly DormantSince,
        DateTime DormantMaintTs,
        string CardLast4)
    {
        public Account ToAccount(IEnumerable<PendingTxn> txns)
        {
            var mine = txns.ToList();
            var lastDate = mine.Count == 0 ? DormantSince : mine.Max(t => t.PostingDate);
            var lastTs = mine.Count == 0 ? DormantMaintTs : mine.Where(t => t.PostingDate == lastDate).Max(t => t.PostedTs);
            return new Account(AccountNbr, MemberNbr, Category, ProductCode, Description, OpenDate, null, CurrentBalance, AvailableBalance,
                InterestRate, SqlDecimal.ConvertToPrecScale(AccruedInterest, 31, 8), mine.Count == 0 ? "D" : "O", lastDate, lastTs);
        }
    }

    private sealed class PendingTxn(AccountState account, string code, DateOnly postingDate, DateOnly effectiveDate, DateTime postedTs, decimal amount, string? description, string channel)
    {
        public AccountState Account { get; } = account;

        public string Code { get; } = code;

        public DateOnly PostingDate { get; } = postingDate;

        public DateOnly EffectiveDate { get; } = effectiveDate;

        public DateTime PostedTs { get; } = postedTs;

        public decimal Amount { get; } = amount;

        public string? Description { get; } = description;

        public string Channel { get; } = channel;

        public (string Name, string Mcc)? Merchant { get; init; }

        public PendingTxn? ReversalOf { get; set; }

        public long TxnId { get; set; }

        public string? CardRef { get; set; }
    }
}
