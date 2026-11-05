using CreditUnion.Batch.TestData.Canonical;
using CreditUnion.Batch.TestData.KeyStone;
using CreditUnion.Batch.TestData.Scenarios;

namespace CreditUnion.Batch.TestHarness.Scenarios;

/// <summary>An input file destined for blob storage.</summary>
public sealed record ScenarioBlob(string Container, string Name, byte[] Content);

/// <summary>
/// A scenario as committed under tests/TestData: <c>inputs/{name}</c> (what to load and how to run it)
/// and <c>golden/{name}</c> (what the outputs must be). Inputs of the performance scenario are not
/// committed; they are regenerated from the seed into tests/TestData/generated on first use.
/// </summary>
public sealed class Scenario
{
    private Scenario(string name, string inputsDirectory, string goldenDirectory, ScenarioInputFile input, GoldenManifest golden)
    {
        Name = name;
        InputsDirectory = inputsDirectory;
        GoldenDirectory = goldenDirectory;
        Input = input;
        Golden = golden;
    }

    public string Name { get; }

    public string InputsDirectory { get; }

    public string GoldenDirectory { get; }

    public ScenarioInputFile Input { get; }

    public GoldenManifest Golden { get; }

    public IReadOnlyList<RunInput> Runs => Input.Runs;

    public static IReadOnlyList<string> CommittedNames =>
        [.. ScenarioCatalog.All.Where(s => s.CommitInputs).Select(s => s.Name)];

    public static Scenario Load(string name)
    {
        var repo = RepositoryPaths.Root;
        var testData = ScenarioFileSystem.TestDataRoot(repo);
        var definition = ScenarioCatalog.Get(name);
        var inputs = Path.Combine(testData, "inputs", name);
        if (!definition.CommitInputs)
        {
            inputs = Path.Combine(testData, "generated", "inputs", name);
            if (!File.Exists(Path.Combine(inputs, "scenario.json")))
            {
                var generated = ScenarioGenerator.Generate(definition, ScenarioGenerator.LoadContractMapping(repo));
                ScenarioFileSystem.WriteInputs(generated, Path.Combine(testData, "generated"));
            }
        }

        var golden = Path.Combine(testData, "golden", name);
        return new Scenario(name, inputs, golden,
            ScenarioJson.Deserialize<ScenarioInputFile>(ReadText(Path.Combine(inputs, "scenario.json"))),
            ScenarioJson.Deserialize<GoldenManifest>(ReadText(Path.Combine(golden, "manifest.json"))));
    }

    /// <summary>The scenario's rows for a KeyStone table (header only when the scenario has none).</summary>
    public TableData InputTable(Db2TableDefinition table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var path = Path.Combine(InputsDirectory, "db2", table.FileName);
        return File.Exists(path) ? CanonicalCsv.ReadFile(path, table.Name) : TableData.Empty(table.Name, [.. table.Columns.Select(c => c.Name)]);
    }

    public IEnumerable<ScenarioBlob> InputBlobs() => Input.Blobs.Select(relative =>
    {
        var parts = relative.Split('/', 3);
        return new ScenarioBlob(parts[1], parts[2], File.ReadAllBytes(Path.Combine(InputsDirectory, relative.Replace('/', Path.DirectorySeparatorChar))));
    });

    /// <summary>The KeyBridge response body the stub must serve for this scenario.</summary>
    public string KeyBridgeMappingJson => ReadText(Input.KeyBridgeMapping is { } file
        ? Path.Combine(InputsDirectory, file.Replace('/', Path.DirectorySeparatorChar))
        : Path.Combine(RepositoryPaths.Root, ScenarioGenerator.ContractMappingPath.Replace('/', Path.DirectorySeparatorChar)));

    public TableExpectation GoldenTableExpectation(string table) =>
        Golden.Tables.SingleOrDefault(t => t.Name == table) ?? throw new ArgumentException($"{Name} has no golden table {table}.", nameof(table));

    /// <summary>Golden rows; for scenarios that commit only checksums this throws, use the expectation's checksum instead.</summary>
    public TableData GoldenTable(string table)
    {
        var expectation = GoldenTableExpectation(table);
        return expectation.File is { } file
            ? CanonicalCsv.ReadFile(Path.Combine(GoldenDirectory, file.Replace('/', Path.DirectorySeparatorChar)), table)
            : throw new InvalidOperationException($"{Name} commits only the row count and checksum of {table}.");
    }

    public string GoldenText(string relativePath) => ReadText(Path.Combine(GoldenDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public RunExpectation ExpectedRun(int run) => Golden.Runs.Single(r => r.Run == run);

    public override string ToString() => Name;

    private static string ReadText(string path) => File.ReadAllText(path, CanonicalCsv.Encoding);
}
