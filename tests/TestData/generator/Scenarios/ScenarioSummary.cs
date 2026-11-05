using System.Globalization;
using System.Text;
using static CreditUnion.Batch.TestData.Canonical.CanonicalValue;

namespace CreditUnion.Batch.TestData.Scenarios;

/// <summary>Human-readable summary of a scenario's inputs and golden outputs, for the review in tests/TestData/REVIEW.md.</summary>
public static class ScenarioSummary
{
    public static string Build(GeneratedScenario g, GoldenManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(manifest);
        var s = g.Definition;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"# {s.Name}\n\n{s.Description}\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Acceptance tests: {string.Join(", ", s.AcceptanceTests)}\n- Seed: {s.Seed}; generator version {ScenarioGenerator.GeneratorVersion}\n");
        sb.Append(CultureInfo.InvariantCulture, $"- KeyBridge mapping: {(g.Inputs.KeyBridgeOverridden ? "scenario override (inputs/" + s.Name + "/keybridge/gl-mappings.json)" : "contract default (" + ScenarioGenerator.ContractMappingPath + ")")}, version {g.Inputs.KeyBridge.Version}\n\n");

        sb.Append("## Runs and expected outcomes\n\n| Run | Entry point | Business date | Trigger (UTC) | Status | Error code | Failed step | Not run | KeyBridge calls | Alerts |\n| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (var (spec, run) in s.Runs.Zip(manifest.Runs))
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {run.Run} | {run.Pipeline} | {run.BusinessDate} | {spec.TriggerTimeUtc?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "manual"} | {run.Status} | {run.ErrorCode ?? "-"} | {run.FailedStep ?? "-"} | ")
              .Append(CultureInfo.InvariantCulture, $"{(run.PipelinesNotRun.Count == 0 ? "-" : string.Join(", ", run.PipelinesNotRun))} | {run.KeyBridgeRequests} | {run.Alerts.Count} |\n");
        }

        var messages = manifest.Runs.SelectMany(r => r.Messages.Select(m => (r.Run, m))).ToList();
        if (messages.Count > 0)
        {
            sb.Append("\nExpected error messages (ops.BatchRun.ErrorMessage contains):\n\n");
            foreach (var (run, m) in messages)
            {
                sb.Append(CultureInfo.InvariantCulture, $"- run {run}, {m.Pipeline} / {m.StepName ?? "-"}: `{m.Contains}`\n");
            }
        }

        sb.Append("\n## Inputs\n\n| Db2 table | Rows | Rows per date (date column) |\n| --- | ---: | --- |\n");
        var ks = g.Inputs.KeyStone;
        Row(sb, "KS.TXN_CODE (added)", ks.ExtraTxnCodes.Count, null);
        Row(sb, "KS.MEMBER", ks.Members.Count, null);
        var activityDates = ks.Transactions.Select(t => t.PostingDate).Concat(s.Runs.Select(r => r.BusinessDate)).ToHashSet();
        var dormant = ks.Accounts.Count(a => !activityDates.Contains(a.LastActivityDate));
        Row(sb, "KS.ACCOUNT", ks.Accounts.Count, ks.Accounts.Where(a => activityDates.Contains(a.LastActivityDate))
            .GroupBy(a => a.LastActivityDate).Select(x => (x.Key, x.Count())), dormant > 0 ? $"; {dormant} dormant (older dates)" : string.Empty);
        Row(sb, "KS.POSTED_TXN", ks.Transactions.Count, ks.Transactions.GroupBy(t => t.PostingDate).Select(x => (x.Key, x.Count())));
        Row(sb, "KS.GL_ENTRY", ks.GlEntries.Count, ks.GlEntries.GroupBy(e => e.PostingDate).Select(x => (x.Key, x.Count())));

        if (g.Inputs.SettlementFiles.Count > 0)
        {
            sb.Append("\n| Settlement file | Detail rows | Trailer count | Trailer total | Signed total (SALE - RFND) |\n| --- | ---: | ---: | ---: | ---: |\n");
            foreach (var f in g.Inputs.SettlementFiles)
            {
                var signed = f.Details.Sum(d => (d.TxnType == "SALE" ? 1 : -1) * ParseDecimal(d.Amount));
                sb.Append(CultureInfo.InvariantCulture, $"| card-landing/{f.LandingPath} | {f.Details.Count} | {f.TrailerCount} | {f.TrailerTotal} | {Decimal(signed, 2)} |\n");
            }
        }

        var others = g.Inputs.OtherBlobs.Select(b => b.Path).ToList();
        if (others.Count > 0)
        {
            sb.Append("\nOther blobs (must be left untouched): ").Append(string.Join(", ", others.Select(p => $"`{p}`"))).Append('\n');
        }

        sb.Append("\n## Golden tables\n\n| Table | Rows | SHA-256 (ADR 0006) |\n| --- | ---: | --- |\n");
        foreach (var t in manifest.Tables)
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {(t.File is null ? t.Name : $"[{t.Name}]({t.File})")} | {t.RowCount} | `{t.Sha256}` |\n");
        }

        foreach (var date in s.Runs.Select(r => r.BusinessDate).Distinct().Order())
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n## Business date {Date(date)}\n\n");
            if (g.Oracle.DailyGlTotals.Any(d => d.BusinessDate == date) || g.Oracle.CoreCardSettlements.Any(c => c.SettlementDate == date))
            {
                var card = g.Oracle.CardTotals(date);
                sb.Append(CultureInfo.InvariantCulture, $"Card check: settlement {Decimal(card.Settlement, 2)}, KeyStone {Decimal(card.KeyStone, 2)}, difference {Decimal(card.Difference, 2)}.\n\n");
            }

            var totals = g.Oracle.DailyGlTotals.Where(d => d.BusinessDate == date).ToList();
            if (totals.Count > 0)
            {
                sb.Append("| GL account | Debit total | Credit total | Net | Txns | KeyStone debit | KeyStone credit |\n| --- | ---: | ---: | ---: | ---: | ---: | ---: |\n");
                var detail = g.Oracle.GlDetail(date);
                foreach (var t in totals)
                {
                    var ksDebit = detail.Single(x => x.Account == t.GlAccountNbr && x.Side == "DEBIT").KeyStone;
                    var ksCredit = detail.Single(x => x.Account == t.GlAccountNbr && x.Side == "CREDIT").KeyStone;
                    sb.Append(CultureInfo.InvariantCulture, $"| {t.GlAccountNbr.TrimEnd()} | {Decimal(t.DebitTotal, 2)} | {Decimal(t.CreditTotal, 2)} | {Decimal(t.NetAmount, 2)} | {t.TxnCount} | {Decimal(ksDebit, 2)} | {Decimal(ksCredit, 2)} |\n");
                }
            }

            foreach (var e in g.Oracle.ReconExceptions.Where(e => e.BusinessDate == date))
            {
                sb.Append(CultureInfo.InvariantCulture, $"\nException: {e.CheckName} {e.ItemKey}, KeyStone {Decimal(e.KeyStoneAmount, 2)}, compared {Decimal(e.ComparedAmount, 2)}, difference {Decimal(e.Difference, 2)}, alerted in run {e.AlertRun?.ToString(CultureInfo.InvariantCulture) ?? "-"}.\n");
            }
        }

        if (manifest.Schedule is { Count: > 0 } schedule)
        {
            sb.Append("\n## Trigger schedule (TR_Nightly_0300_Pacific)\n\n| Local date | Fires at (local) | Fires at (UTC) | businessDate |\n| --- | --- | --- | --- |\n");
            foreach (var x in schedule)
            {
                sb.Append(CultureInfo.InvariantCulture, $"| {x.LocalDate} | {x.FireTimeLocal} | {x.FireTimeUtc} | {x.BusinessDate} |\n");
            }
        }

        if (g.Inputs.ReviewNotes is { } notes)
        {
            sb.Append('\n').Append(notes);
        }

        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string table, int count, IEnumerable<(DateOnly Date, int Count)>? byDate, string suffix = "") =>
        sb.Append(CultureInfo.InvariantCulture, $"| {table} | {count} | {(byDate is null ? "-" : string.Join(", ", byDate.OrderBy(x => x.Date).Select(x => $"{Date(x.Date)}: {x.Count}")))}{suffix} |\n");
}
