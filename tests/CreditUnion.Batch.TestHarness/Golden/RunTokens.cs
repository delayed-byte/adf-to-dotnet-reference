using System.Globalization;
using CreditUnion.Batch.TestData.Oracle;

namespace CreditUnion.Batch.TestHarness.Golden;

/// <summary>
/// Maps the scenario's batch run IDs (run 1 first) to the <c>{run:N}</c> tokens used in golden blob
/// names, metadata, error manifests, evidence and alerts.
/// </summary>
public sealed class RunTokens(IReadOnlyList<Guid> runIds)
{
    public IReadOnlyList<Guid> RunIds { get; } = runIds ?? throw new ArgumentNullException(nameof(runIds));

    /// <summary>Expected text with tokens replaced by the actual run IDs (lowercase, hyphenated).</summary>
    public string Resolve(string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        for (var i = 0; i < RunIds.Count; i++)
        {
            expected = expected.Replace(BatchOracle.RunToken(i + 1), RunIds[i].ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        return expected;
    }

    /// <summary>Actual text with run IDs (any case) replaced by their tokens.</summary>
    public string Tokenize(string actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        for (var i = 0; i < RunIds.Count; i++)
        {
            actual = actual.Replace(RunIds[i].ToString("D", CultureInfo.InvariantCulture), BatchOracle.RunToken(i + 1), StringComparison.OrdinalIgnoreCase);
        }

        return actual;
    }
}
