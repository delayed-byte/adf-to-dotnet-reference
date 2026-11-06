using System.Security.Cryptography;
using CreditUnion.Batch.TestData.Canonical;
using CreditUnion.Batch.TestData.Golden;
using CreditUnion.Batch.TestData.Oracle;
using CreditUnion.Batch.TestHarness.Scenarios;
using Shouldly;

namespace CreditUnion.Batch.AcceptanceTests.Support;

/// <summary>
/// The idempotency check every acceptance test ends with (PRD: run the batch twice and assert the
/// outputs are unchanged; ADR 0006). A rerun gets new batch run IDs, as an ADF rerun would. It must
/// end the same way, post no alert, and leave every table unchanged except the run-scoped audit
/// trail (ops.BatchRun and ops.CardFileLog append by design), and every blob unchanged except the
/// rerun's own evidence file.
/// </summary>
public static class Idempotency
{
    private static readonly HashSet<string> AuditTables = [GoldenTables.BatchRun.Name, GoldenTables.CardFileLog.Name];

    public static async Task RerunShouldChangeNothingAsync(BatchHost host, IReadOnlyList<Guid> runIds, IReadOnlyList<PipelineRun> firstRuns, Func<Task<IReadOnlyList<PipelineRun>>> rerun)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(firstRuns);
        ArgumentNullException.ThrowIfNull(rerun);
        var before = await SnapshotAsync(host, runIds);

        var reruns = await rerun();

        reruns.Count.ShouldBe(firstRuns.Count);
        for (var i = 0; i < reruns.Count; i++)
        {
            (reruns[i].Succeeded, reruns[i].ErrorCode).ShouldBe((firstRuns[i].Succeeded, firstRuns[i].ErrorCode), $"the rerun ends as the first run did: {reruns[i].Describe()}");
        }

        reruns.SelectMany(r => r.Alerts).ShouldBeEmpty("a rerun must not alert again for exceptions already alerted");
        var after = await SnapshotAsync(host, runIds);
        var problems = new List<string>();
        foreach (var (table, data) in before.Tables)
        {
            var comparison = TableComparison.Compare(data, after.Tables[table], host.Scenario.GoldenTableExpectation(table).KeyColumns);
            if (!comparison.IsMatch)
            {
                problems.Add("rerun changed " + comparison.Report());
            }
        }

        problems.AddRange(before.Blobs.Where(b => !after.Blobs.Contains(b)).Select(b => $"rerun changed or removed blob {b.Split('|')[0]}"));
        problems.AddRange(after.Blobs.Where(b => !before.Blobs.Contains(b) && !b.StartsWith(BatchOracle.EvidenceContainer + "/", StringComparison.Ordinal))
            .Select(b => $"rerun added blob {b.Split('|')[0]}"));
        problems.ShouldBeEmpty();
    }

    /// <summary>Non-audit golden tables of the scenario and every blob (path, content hash, metadata).</summary>
    public static async Task<StateSnapshot> SnapshotAsync(BatchHost host, IReadOnlyList<Guid> runIds)
    {
        ArgumentNullException.ThrowIfNull(host);
        var ct = TestContext.Current.CancellationToken;
        var tables = new Dictionary<string, TableData>(StringComparer.Ordinal);
        foreach (var expectation in host.Scenario.Golden.Tables.Where(t => !AuditTables.Contains(t.Name)))
        {
            tables[expectation.Name] = await host.Environment.Reporting.SnapshotAsync(GoldenTables.Get(expectation.Name), runIds, ct);
        }

        var blobs = (await host.Environment.Blobs.ListAsync(ct)).Select(b =>
            $"{b.Container}/{b.Name}|{Convert.ToHexStringLower(SHA256.HashData(b.Content))}|{string.Join(",", b.Metadata.Select(m => m.Key + "=" + m.Value))}");
        return new StateSnapshot(tables, blobs.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>Asserts two snapshots are identical (AT-16: a rerun of an earlier date touches nothing).</summary>
    public static void ShouldBeUnchanged(StateSnapshot before, StateSnapshot after, Scenario scenario, string because)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(scenario);
        var problems = before.Tables
            .Select(t => TableComparison.Compare(t.Value, after.Tables[t.Key], scenario.GoldenTableExpectation(t.Key).KeyColumns))
            .Where(c => !c.IsMatch).Select(c => c.Report()).ToList();
        problems.AddRange(before.Blobs.Except(after.Blobs).Select(b => "changed or removed blob " + b.Split('|')[0]));
        problems.AddRange(after.Blobs.Except(before.Blobs).Where(b => !b.StartsWith(BatchOracle.EvidenceContainer + "/", StringComparison.Ordinal))
            .Select(b => "added blob " + b.Split('|')[0]));
        problems.ShouldBeEmpty(because);
    }
}

public sealed record StateSnapshot(IReadOnlyDictionary<string, TableData> Tables, IReadOnlySet<string> Blobs);
