using CreditUnion.Batch.TestData.Card;
using CreditUnion.Batch.TestData.KeyBridge;
using CreditUnion.Batch.TestData.KeyStone;
using CreditUnion.Batch.TestData.Oracle;
using CreditUnion.Batch.TestData.Reporting;

namespace CreditUnion.Batch.TestData.Scenarios;

/// <summary>Everything a scenario puts in front of the batch: Db2 rows, files in blob storage and the KeyBridge response.</summary>
public sealed record ScenarioInputs(
    KeyStoneDataset KeyStone,
    IReadOnlyList<SettlementFile> SettlementFiles,
    IReadOnlyList<BlobItem> OtherBlobs,
    GlMappingResponse KeyBridge,
    bool KeyBridgeOverridden)
{
    /// <summary>Optional reviewer worksheet appended to summary.md (for example the rounding cases of AT-10).</summary>
    public string? ReviewNotes { get; init; }

    public IEnumerable<BlobItem> AllBlobs => SettlementFiles
        .Select(f => new BlobItem(BatchOracle.LandingContainer, f.LandingPath, f.RenderBytes(), new Dictionary<string, string>()))
        .Concat(OtherBlobs);
}

/// <summary>
/// A test scenario: fixed inputs produced from a seed, the runs to execute, and the acceptance tests
/// that use it. Golden outputs are the oracle's state after <see cref="Runs"/>.
/// </summary>
public sealed record ScenarioDefinition(
    string Name,
    string Description,
    IReadOnlyList<string> AcceptanceTests,
    ulong Seed,
    IReadOnlyList<RunSpec> Runs,
    Func<ScenarioDefinition, GlMappingResponse, ScenarioInputs> Build)
{
    /// <summary>False for the performance scenario: inputs are regenerated on demand into tests/TestData/generated.</summary>
    public bool CommitInputs { get; init; } = true;

    /// <summary>False when only row counts and checksums are committed (too large to review row by row).</summary>
    public bool CommitTableFiles { get; init; } = true;

    public IReadOnlyList<ScheduleExpectation>? Schedule { get; init; }

    public string Category { get; init; } = "Default";
}
