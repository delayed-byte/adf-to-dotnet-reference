using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CreditUnion.Batch.TestData.Canonical;
using CreditUnion.Batch.TestData.Golden;
using CreditUnion.Batch.TestData.Oracle;
using CreditUnion.Batch.TestHarness.Scenarios;
using CreditUnion.Batch.TestHarness.Sql;
using CreditUnion.Batch.TestHarness.Storage;

namespace CreditUnion.Batch.TestHarness.Golden;

/// <summary>Raised when actual output differs from golden output; the message is the full comparison report.</summary>
public sealed class GoldenMismatchException : Exception
{
    public GoldenMismatchException()
    {
    }

    public GoldenMismatchException(string message)
        : base(message)
    {
    }

    public GoldenMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Result of comparing one golden table: row counts, checksums and the first mismatches.</summary>
public sealed record GoldenTableResult(string Table, bool IsMatch, string Report);

/// <summary>
/// Compares the state of the environment with a scenario's golden outputs (ADR 0006): tables (row
/// counts, order-independent SHA-256, keyed column diffs listing the first 20 mismatches), blobs
/// (names, content hashes, metadata), error manifests, evidence files and alert payloads.
/// </summary>
public static partial class GoldenAssert
{
    /// <summary>Evidence sections that are deterministic; Run and BatchRun carry times and run order and are not compared.</summary>
    public static IReadOnlyList<string> EvidenceSections { get; } = ["Files", "RowCounts", "Checksums", "Reconciliation", "Exceptions"];

    /// <param name="pipelines">Compare only tables owned by these pipelines (and their ops.BatchRun events); null compares everything.</param>
    public static async Task<IReadOnlyList<GoldenTableResult>> CompareTablesAsync(ReportingDatabase database, Scenario scenario, IReadOnlyList<Guid> runIds, IReadOnlyCollection<string>? pipelines = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(scenario);
        var results = new List<GoldenTableResult>();
        foreach (var expectation in scenario.Golden.Tables)
        {
            var definition = GoldenTables.Get(expectation.Name);
            if (pipelines is not null && definition.OwnerPipeline is not null && !pipelines.Contains(definition.OwnerPipeline))
            {
                continue;
            }

            var actual = await database.SnapshotAsync(definition, runIds, cancellationToken).ConfigureAwait(false);
            if (expectation.File is null)
            {
                var checksum = TableChecksum.Compute(actual);
                var ok = actual.RowCount == expectation.RowCount && checksum == expectation.Sha256;
                results.Add(new GoldenTableResult(expectation.Name, ok,
                    $"{expectation.Name}: rows expected {expectation.RowCount}, actual {actual.RowCount}; checksum expected {expectation.Sha256[..12]}..., actual {checksum[..12]}...{(ok ? " - match" : " - MISMATCH (row data not committed)")}"));
                continue;
            }

            var expected = scenario.GoldenTable(expectation.Name);
            if (definition == GoldenTables.BatchRun && pipelines is not null)
            {
                expected = expected.Where("PipelineName", [.. pipelines]);
                actual = actual.Where("PipelineName", [.. pipelines]);
            }

            var comparison = TableComparison.Compare(expected, actual, expectation.KeyColumns);
            results.Add(new GoldenTableResult(expectation.Name, comparison.IsMatch, comparison.Report()));
        }

        return results;
    }

    public static async Task TablesMatchAsync(ReportingDatabase database, Scenario scenario, IReadOnlyList<Guid> runIds, IReadOnlyCollection<string>? pipelines = null, CancellationToken cancellationToken = default)
    {
        var results = await CompareTablesAsync(database, scenario, runIds, pipelines, cancellationToken).ConfigureAwait(false);
        ThrowIfAny(scenario, "tables", results.Where(r => !r.IsMatch).Select(r => r.Report), results.Select(r => r.Report));
    }

    /// <summary>Blob names, content and metadata after the runs. Evidence files are compared section by section.</summary>
    public static async Task<IReadOnlyList<string>> BlobDifferencesAsync(BlobStore store, Scenario scenario, IReadOnlyList<Guid> runIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scenario);
        var tokens = new RunTokens(runIds);
        var actual = (await store.ListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(b => $"{b.Container}/{b.Name}", StringComparer.Ordinal);
        var differences = new List<string>();
        foreach (var e in scenario.Golden.Blobs)
        {
            var path = $"{e.Container}/{tokens.Resolve(e.Name)}";
            if (!actual.Remove(path, out var blob))
            {
                differences.Add($"missing blob {path}");
                continue;
            }

            var actualMetadata = blob.Metadata.ToDictionary(kv => kv.Key, kv => tokens.Tokenize(kv.Value), StringComparer.Ordinal);
            if (!actualMetadata.OrderBy(kv => kv.Key, StringComparer.Ordinal).SequenceEqual(e.Metadata.OrderBy(kv => kv.Key, StringComparer.Ordinal)))
            {
                differences.Add($"{path}: metadata expected {{{Show(e.Metadata)}}}, actual {{{Show(actualMetadata)}}}");
            }

            if (e.Sha256 is { } sha && !string.Equals(sha, Convert.ToHexStringLower(SHA256.HashData(blob.Content)), StringComparison.Ordinal))
            {
                differences.Add($"{path}: content differs from the golden input file (SHA-256)");
            }
            else if (e.GoldenFile is { } file)
            {
                var actualText = CanonicalCsv.Encoding.GetString(blob.Content);
                var found = e.Container == BatchOracle.EvidenceContainer
                    ? EvidenceDifferences(scenario.GoldenText(file), actualText, tokens)
                    : JsonEquivalence.Differences(scenario.GoldenText(file), tokens.Tokenize(actualText), unorderedArrays: true);
                differences.AddRange(found.Select(d => $"{path}: {d}"));
            }
        }

        differences.AddRange(actual.Keys.Select(p => $"unexpected blob {p}"));
        return differences;
    }

    public static async Task BlobsMatchAsync(BlobStore store, Scenario scenario, IReadOnlyList<Guid> runIds, CancellationToken cancellationToken = default)
    {
        var differences = await BlobDifferencesAsync(store, scenario, runIds, cancellationToken).ConfigureAwait(false);
        ThrowIfAny(scenario, "blobs", differences, []);
    }

    /// <summary>Compares an evidence file with its golden version, as multisets per deterministic section.</summary>
    public static IReadOnlyList<string> EvidenceDifferences(string expectedJson, string actualJson, RunTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        static List<(string Section, string Item, string Value)> Entries(string json)
        {
            using var document = JsonDocument.Parse(json);
            return
            [
                .. document.RootElement.EnumerateArray().Select(e =>
                    (e.GetProperty("section").GetString()!, e.GetProperty("item").GetString()!, e.GetProperty("value").GetString() ?? string.Empty)),
            ];
        }

        var expected = Entries(expectedJson);
        var actual = Entries(tokens.Tokenize(actualJson)).Select(e => e with { Value = NormalizeEvidenceValue(e.Value) }).ToList();
        var differences = new List<string>();
        foreach (var section in EvidenceSections)
        {
            var e = expected.Where(x => x.Section == section).Select(x => $"{x.Item} = {x.Value}").Order(StringComparer.Ordinal).ToList();
            var a = actual.Where(x => x.Section == section).Select(x => $"{x.Item} = {x.Value}").Order(StringComparer.Ordinal).ToList();
            differences.AddRange(e.Except(a, StringComparer.Ordinal).Select(x => $"evidence {section}: missing [{x}]"));
            differences.AddRange(a.Except(e, StringComparer.Ordinal).Select(x => $"evidence {section}: unexpected [{x}]"));
        }

        return [.. differences.Take(TableComparison.DefaultMaxReported)];
    }

    /// <summary>The webhook bodies received for one run, compared in order with the golden alert payloads.</summary>
    public static IReadOnlyList<string> AlertDifferences(Scenario scenario, int run, IReadOnlyList<string> actualBodies, RunTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(actualBodies);
        ArgumentNullException.ThrowIfNull(tokens);
        var expected = scenario.ExpectedRun(run).Alerts;
        var differences = new List<string>();
        if (expected.Count != actualBodies.Count)
        {
            differences.Add($"run {run}: expected {expected.Count} alert(s), received {actualBodies.Count}");
        }

        for (var i = 0; i < Math.Min(expected.Count, actualBodies.Count); i++)
        {
            differences.AddRange(JsonEquivalence.Differences(scenario.GoldenText(expected[i]), tokens.Tokenize(actualBodies[i])).Select(d => $"alert {i + 1}: {d}"));
        }

        return differences;
    }

    private static string NormalizeEvidenceValue(string value) =>
        UtcValue().Replace(value, m => m.Groups["v"].Value == "-" ? m.Value : $"{m.Groups["k"].Value}={BatchOracle.UtcToken}");

    private static void ThrowIfAny(Scenario scenario, string what, IEnumerable<string> failures, IEnumerable<string> context)
    {
        var list = failures.ToList();
        if (list.Count == 0)
        {
            return;
        }

        var report = new StringBuilder($"Scenario {scenario.Name}: {what} differ from golden output (tests/TestData/golden/{scenario.Name}).\n");
        foreach (var line in context.Any() ? context : list)
        {
            report.Append(line).Append('\n');
        }

        throw new GoldenMismatchException(report.ToString());
    }

    private static string Show(IEnumerable<KeyValuePair<string, string>> metadata) => string.Join(", ", metadata.Select(kv => $"{kv.Key}={kv.Value}"));

    [GeneratedRegex("(?<k>detectedUtc|alertSentUtc)=(?<v>[^;]+)", RegexOptions.CultureInvariant)]
    private static partial Regex UtcValue();
}
