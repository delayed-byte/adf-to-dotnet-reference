namespace CreditUnion.Batch.UnitTests;

internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Adf => Path.Combine(Root, "adf");

    public static string ReportingProcedures => Path.Combine(Root, "db", "reporting", "procedures");

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AdfToDotnet.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root (AdfToDotnet.sln) not found above the test output folder.");
    }
}
