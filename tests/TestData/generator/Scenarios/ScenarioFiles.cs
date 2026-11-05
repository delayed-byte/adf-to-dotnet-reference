using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CreditUnion.Batch.TestData.Oracle;

namespace CreditUnion.Batch.TestData.Scenarios;

// JSON documents written by the generator and read by the harness (ADR 0006).
// inputs/{scenario}/scenario.json says how to set up and run a scenario;
// golden/{scenario}/manifest.json says what the outputs must be.

public sealed record RunInput(int Run, string Pipeline, string BusinessDate, string? TriggerTimeUtc, string Tolerance);

public sealed record ScenarioInputFile(
    string Scenario,
    string Description,
    IReadOnlyList<string> AcceptanceTests,
    ulong Seed,
    bool InputsCommitted,
    string? KeyBridgeMapping,
    IReadOnlyList<string> Db2Tables,
    IReadOnlyList<string> Blobs,
    IReadOnlyList<RunInput> Runs);

public sealed record TableExpectation(string Name, string? File, int RowCount, string Sha256, IReadOnlyList<string> KeyColumns);

public sealed record RunExpectation(
    int Run,
    string Pipeline,
    string BusinessDate,
    string Status,
    string? ErrorCode,
    string? FailedStep,
    IReadOnlyList<string> PipelinesRun,
    IReadOnlyList<string> PipelinesNotRun,
    IReadOnlyList<IReadOnlyList<string>> Stages,
    IReadOnlyList<MessageExpectation> Messages,
    int KeyBridgeRequests,
    IReadOnlyList<string> Alerts,
    string? Evidence);

/// <summary>A blob that must exist after the runs. <see cref="Name"/> and metadata values may contain <c>{run:N}</c> tokens.</summary>
public sealed record BlobExpectation(string Container, string Name, string? Sha256, IReadOnlyDictionary<string, string> Metadata, string? GoldenFile);

/// <summary>The schedule trigger fires once at 03:00 Pacific on every local date; businessDate is the previous Pacific day.</summary>
public sealed record ScheduleExpectation(string LocalDate, string FireTimeLocal, string FireTimeUtc, string BusinessDate);

public sealed record GoldenManifest(
    string Scenario,
    int GeneratorVersion,
    IReadOnlyList<TableExpectation> Tables,
    IReadOnlyList<RunExpectation> Runs,
    IReadOnlyList<BlobExpectation> Blobs,
    IReadOnlyList<ScheduleExpectation>? Schedule);

public static class ScenarioJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException($"Empty {typeof(T).Name} document.");
}
