using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CreditUnion.Batch.TestData.Canonical;
using CreditUnion.Batch.TestData.Golden;
using CreditUnion.Batch.TestData.KeyBridge;
using CreditUnion.Batch.TestData.KeyStone;
using CreditUnion.Batch.TestData.Oracle;
using CreditUnion.Batch.TestData.Reporting;
using static CreditUnion.Batch.TestData.Canonical.CanonicalValue;

namespace CreditUnion.Batch.TestData.Scenarios;

/// <summary>A generated scenario: its files (paths relative to tests/TestData, '/' separated) and the oracle state behind them.</summary>
public sealed class GeneratedScenario(ScenarioDefinition definition, ScenarioInputs inputs, BatchOracle oracle, IReadOnlyList<RunOutcome> outcomes)
{
    public ScenarioDefinition Definition { get; } = definition;

    public ScenarioInputs Inputs { get; } = inputs;

    public BatchOracle Oracle { get; } = oracle;

    public IReadOnlyList<RunOutcome> Outcomes { get; } = outcomes;

    public SortedDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, TableData> GoldenTables { get; } = new(StringComparer.Ordinal);

    public string InputsFolder => "inputs/" + Definition.Name;

    public string GoldenFolder => "golden/" + Definition.Name;
}

/// <summary>Produces the input and golden files of a scenario (ADR 0006). Pure: no clock, no randomness beyond the seed, no I/O.</summary>
public static class ScenarioGenerator
{
    /// <summary>Bump when a change to the generator deliberately changes golden output.</summary>
    public const int GeneratorVersion = 1;

    public const string ContractMappingPath = "contracts/keybridge/__files/gl-mappings.json";

    public static GlMappingResponse LoadContractMapping(string repoRoot) =>
        GlMappingResponse.Parse(File.ReadAllText(Path.Combine(repoRoot, ContractMappingPath), CanonicalCsv.Encoding));

    public static GeneratedScenario Generate(ScenarioDefinition scenario, GlMappingResponse contractMapping)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var inputs = scenario.Build(scenario, contractMapping);
        var initialBlobs = inputs.AllBlobs.ToList();
        var oracle = new BatchOracle(inputs.KeyStone, initialBlobs, inputs.KeyBridge);
        var outcomes = scenario.Runs.Select(oracle.Run).ToList();
        var g = new GeneratedScenario(scenario, inputs, oracle, outcomes);

        WriteInputs(g, initialBlobs);
        var tables = GoldenTablesFor(scenario).Select(t => (Definition: t, Data: Build(t, oracle))).ToList();
        foreach (var (definition, data) in tables)
        {
            g.GoldenTables[definition.Name] = data;
            if (scenario.CommitTableFiles)
            {
                Add(g, $"{g.GoldenFolder}/tables/{definition.FileName}", CanonicalCsv.Write(data));
            }
        }

        var runs = new List<RunExpectation>();
        foreach (var o in outcomes)
        {
            var alerts = o.Alerts.Select((json, i) => (Path: $"alerts/run-{o.Spec.Run}-alert-{i + 1}.json", Json: json)).ToList();
            alerts.ForEach(a => Add(g, $"{g.GoldenFolder}/{a.Path}", a.Json));
            string? evidence = null;
            if (o.Evidence is not null)
            {
                evidence = $"evidence/run-{o.Spec.Run}.json";
                var array = new JsonArray([.. o.Evidence.Select(e => (JsonNode)new JsonObject { ["section"] = e.Section, ["item"] = e.Item, ["value"] = e.Value })]);
                Add(g, $"{g.GoldenFolder}/{evidence}", array.ToJsonString(ScenarioJson.Options).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
            }

            runs.Add(new RunExpectation(o.Spec.Run, o.Spec.Pipeline, Date(o.Spec.BusinessDate), o.Status, o.ErrorCode, o.FailedStep,
                o.PipelinesRun, o.PipelinesNotRun, Stages(o), o.Messages, o.KeyBridgeRequests, [.. alerts.Select(a => a.Path)], evidence));
        }

        var blobs = new List<BlobExpectation>();
        foreach (var blob in oracle.Blobs)
        {
            string? goldenFile = null;
            if (blob.Container == BatchOracle.EvidenceContainer)
            {
                goldenFile = $"evidence/run-{RunOf(blob.Name)}.json";
            }
            else if (blob.Name.EndsWith(".errors.json", StringComparison.Ordinal))
            {
                goldenFile = $"blobs/{blob.Container}/{blob.Name}";
                g.Files[$"{g.GoldenFolder}/{goldenFile}"] = blob.Content;
            }

            blobs.Add(new BlobExpectation(blob.Container, blob.Name, goldenFile is null ? Sha256(blob.Content) : null,
                new SortedDictionary<string, string>(blob.Metadata.ToDictionary(), StringComparer.Ordinal), goldenFile));
        }

        var manifest = new GoldenManifest(scenario.Name, GeneratorVersion,
            [.. tables.Select(t => new TableExpectation(t.Definition.Name, scenario.CommitTableFiles ? $"tables/{t.Definition.FileName}" : null,
                t.Data.RowCount, TableChecksum.Compute(t.Data), t.Definition.KeyColumns))],
            runs, blobs, scenario.Schedule);
        Add(g, $"{g.GoldenFolder}/manifest.json", ScenarioJson.Serialize(manifest));
        Add(g, $"{g.GoldenFolder}/summary.md", ScenarioSummary.Build(g, manifest));
        return g;
    }

    /// <summary>Golden tables for the pipelines a scenario runs, plus ops.BatchRun.</summary>
    public static IReadOnlyList<GoldenTableDefinition> GoldenTablesFor(ScenarioDefinition scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var pipelines = scenario.Runs.SelectMany(r => r.Pipeline == Pipelines.Master ? Pipelines.Children : [r.Pipeline]).ToHashSet(StringComparer.Ordinal);
        return [.. Golden.GoldenTables.All.Where(t => t.OwnerPipeline is null || pipelines.Contains(t.OwnerPipeline))];
    }

    private static void WriteInputs(GeneratedScenario g, List<BlobItem> blobs)
    {
        var s = g.Definition;
        var db2 = KeyStoneTables.LoadOrder.Select(t => (t, KeyStoneTables.ToTable(t, g.Inputs.KeyStone))).ToList();
        string? mappingFile = g.Inputs.KeyBridgeOverridden ? "keybridge/gl-mappings.json" : null;
        var scenarioFile = new ScenarioInputFile(s.Name, s.Description, s.AcceptanceTests, s.Seed, s.CommitInputs, mappingFile,
            [.. db2.Select(d => "db2/" + d.t.FileName)], [.. blobs.Select(b => $"blobs/{b.Path}")],
            [.. s.Runs.Select(r => new RunInput(r.Run, r.Pipeline, Date(r.BusinessDate),
                r.TriggerTimeUtc?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), r.Tolerance))]);
        Add(g, $"{g.InputsFolder}/scenario.json", ScenarioJson.Serialize(scenarioFile));

        foreach (var (definition, data) in db2)
        {
            Validate(definition.Name, definition.Columns, data);
            Add(g, $"{g.InputsFolder}/db2/{definition.FileName}", CanonicalCsv.Write(data));
        }

        foreach (var blob in blobs)
        {
            g.Files[$"{g.InputsFolder}/blobs/{blob.Path}"] = blob.Content;
        }

        if (mappingFile is not null)
        {
            Add(g, $"{g.InputsFolder}/{mappingFile}", g.Inputs.KeyBridge.ToJson());
        }
    }

    private static TableData Build(GoldenTableDefinition table, BatchOracle o)
    {
        IEnumerable<string?[]> rows = table.Name switch
        {
            "core.PostedTransaction" => o.CoreTransactions.Select(KeyStoneTables.Row),
            "core.Account" => o.CoreAccounts.Select(KeyStoneTables.Row),
            "core.GlEntry" => o.CoreGlEntries.Select(KeyStoneTables.Row),
            "core.CardSettlement" => o.CoreCardSettlements.OrderBy(c => c.ProcessorRefNbr, StringComparer.Ordinal).Select(c => new[]
            {
                c.ProcessorRefNbr, Date(c.SettlementDate), Date(c.TxnDate), c.CardBin, c.CardLast4, Decimal(c.Amount, 2), c.TxnType,
                c.MerchantName, c.Mcc, c.CurrencyCode, c.SourceFile,
            }),
            "ops.CardFileLog" => o.CardFileLog.OrderBy(f => f.Run).ThenBy(f => f.BusinessDate).ThenBy(f => f.SourceFile, StringComparer.Ordinal).Select(f => new[]
            {
                Integer(f.Run), Date(f.BusinessDate), f.SourceFile, f.Outcome, Opt(f.DetailRowCount), Opt(f.TrailerRowCount),
                Opt(f.DetailTotalAmount), Opt(f.TrailerTotalAmount), f.ErrorSummary, f.DestinationPath,
            }),
            "ref.TxnCodeGlMap" => o.GlMap.Select(m => new[] { m.TxnCode, m.GlAccountNbr, m.DrCr, Decimal(m.AllocationRatio, 6), m.Description, m.SourceVersion }),
            "core.DailyGlTotals" => o.DailyGlTotals.OrderBy(d => d.BusinessDate).ThenBy(d => d.GlAccountNbr, StringComparer.Ordinal).Select(d => new[]
            {
                Date(d.BusinessDate), d.GlAccountNbr, Decimal(d.DebitTotal, 2), Decimal(d.CreditTotal, 2), Decimal(d.NetAmount, 2), Integer(d.TxnCount),
            }),
            "ops.ReconExceptions" => o.ReconExceptions.OrderBy(e => e.BusinessDate).ThenBy(e => e.CheckName, StringComparer.Ordinal).ThenBy(e => e.ItemKey, StringComparer.Ordinal).Select(e => new[]
            {
                Date(e.BusinessDate), e.CheckName, e.ItemKey, Decimal(e.KeyStoneAmount, 2), Decimal(e.ComparedAmount, 2), Decimal(e.Difference, 2),
                Decimal(e.Tolerance, 2), e.Description, Integer(e.DetectedRun), Opt(e.AlertRun),
            }),
            "ops.BatchRun" => o.BatchRun.Select(b => new[]
            {
                Integer(b.Run), Date(b.BusinessDate), b.PipelineName, b.StepName, b.Status, b.ObjectName, Opt(b.RecordCount), b.ErrorCode, b.Notes,
            }),
            _ => throw new ArgumentException($"No golden builder for {table.Name}.", nameof(table)),
        };

        var data = new TableData(table.Name, table.ColumnNames, rows);
        Validate(table.Name, table.Columns, data);
        return data;
    }

    private static void Validate(string table, IReadOnlyList<ColumnDefinition> columns, TableData data)
    {
        var problems = data.Rows.SelectMany((row, r) => columns.Select((c, i) => CanonicalValue.Validate(row[i], c) is { } p ? $"{table} row {r + 1}: {p}" : null))
            .OfType<string>().Take(10).ToList();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Generated data does not fit its column types:\n" + string.Join('\n', problems));
        }
    }

    private static IReadOnlyList<IReadOnlyList<string>> Stages(RunOutcome o)
    {
        if (o.Spec.Pipeline != Pipelines.Master)
        {
            return [];
        }

        IReadOnlyList<IReadOnlyList<string>> all = [[Pipelines.Extract, Pipelines.Card], [Pipelines.Map], [Pipelines.Recon]];
        return [.. all.Where(stage => stage.All(o.PipelinesRun.Contains))];
    }

    private static int RunOf(string blobName)
    {
        var start = blobName.IndexOf("{run:", StringComparison.Ordinal) + 5;
        return int.Parse(blobName[start..blobName.IndexOf('}', start)], CultureInfo.InvariantCulture);
    }

    private static string? Opt(int? value) => value is { } v ? Integer(v) : null;

    private static string? Opt(long? value) => value is { } v ? Integer(v) : null;

    private static string? Opt(decimal? value) => value is { } v ? Decimal(v, 2) : null;

    private static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static void Add(GeneratedScenario g, string path, string text) => g.Files[path] = CanonicalCsv.Encoding.GetBytes(text);
}
