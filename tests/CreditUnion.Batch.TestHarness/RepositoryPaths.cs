using CreditUnion.Batch.TestData.Scenarios;

namespace CreditUnion.Batch.TestHarness;

/// <summary>Well-known locations in the repository, found from the test output folder.</summary>
public static class RepositoryPaths
{
    private static readonly Lazy<string> LazyRoot = new(() => ScenarioFileSystem.FindRepositoryRoot());

    public static string Root => LazyRoot.Value;

    public static string Db2Scripts => Path.Combine(Root, "db", "keystone-db2");

    public static string ReportingScripts => Path.Combine(Root, "db", "reporting");

    public static string Db2ExtractUserScript => Path.Combine(Db2Scripts, "custom", "create_extract_user.sh");

    public static string EnvFile => Path.Combine(Root, ".env");
}
