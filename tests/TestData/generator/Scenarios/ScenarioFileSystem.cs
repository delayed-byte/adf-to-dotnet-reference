namespace CreditUnion.Batch.TestData.Scenarios;

/// <summary>Writes generated scenarios to tests/TestData and detects drift between the committed files and the generator.</summary>
public static class ScenarioFileSystem
{
    public static string FindRepositoryRoot(string? start = null)
    {
        for (var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AdfToDotnet.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root (AdfToDotnet.sln) not found.");
    }

    public static string TestDataRoot(string repoRoot) => Path.Combine(repoRoot, "tests", "TestData");

    /// <summary>Folders owned by a scenario; their content is replaced wholesale on regeneration.</summary>
    public static IEnumerable<string> OwnedFolders(GeneratedScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (scenario.Definition.CommitInputs)
        {
            yield return scenario.InputsFolder;
        }

        yield return scenario.GoldenFolder;
    }

    /// <summary>Replaces the scenario's folders under <paramref name="testDataRoot"/> with the generated files.</summary>
    public static void Write(GeneratedScenario scenario, string testDataRoot, string? uncommittedInputsRoot = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        foreach (var folder in OwnedFolders(scenario))
        {
            var full = Path.Combine(testDataRoot, folder);
            if (Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }
        }

        foreach (var (path, content) in scenario.Files)
        {
            var isInput = path.StartsWith(scenario.InputsFolder + "/", StringComparison.Ordinal);
            var root = isInput && !scenario.Definition.CommitInputs ? uncommittedInputsRoot : testDataRoot;
            if (root is null)
            {
                continue;
            }

            var target = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, content);
        }
    }

    /// <summary>Writes only the scenario's input files below <paramref name="root"/> (used for uncommitted inputs).</summary>
    public static void WriteInputs(GeneratedScenario scenario, string root)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        foreach (var (path, content) in scenario.Files.Where(f => f.Key.StartsWith(scenario.InputsFolder + "/", StringComparison.Ordinal)))
        {
            var target = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, content);
        }
    }

    /// <summary>Differences between the committed files and the generated ones (missing, changed, stale). Empty means no drift.</summary>
    public static IReadOnlyList<string> Drift(GeneratedScenario scenario, string testDataRoot)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var problems = new List<string>();
        var expected = scenario.Files.Where(f => scenario.Definition.CommitInputs || !f.Key.StartsWith(scenario.InputsFolder + "/", StringComparison.Ordinal))
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);

        foreach (var (path, content) in expected)
        {
            var full = Path.Combine(testDataRoot, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                problems.Add($"missing: {path}");
            }
            else if (!File.ReadAllBytes(full).AsSpan().SequenceEqual(content))
            {
                problems.Add($"changed: {path}");
            }
        }

        foreach (var folder in OwnedFolders(scenario))
        {
            var full = Path.Combine(testDataRoot, folder);
            if (!Directory.Exists(full))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(testDataRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                if (!expected.ContainsKey(relative))
                {
                    problems.Add($"stale: {relative}");
                }
            }
        }

        return problems;
    }
}
