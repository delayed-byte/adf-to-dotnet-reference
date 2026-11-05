using CreditUnion.Batch.TestData.Scenarios;

// Regenerates tests/TestData/inputs and tests/TestData/golden from the seeded generator (ADR 0006).
//   dotnet run --project tests/TestData/generator                         all committed scenarios
//   dotnet run --project tests/TestData/generator -- --scenario NAME      one scenario (repeatable)
//   dotnet run --project tests/TestData/generator -- --check              report drift, write nothing
// The performance scenario is generated only when named; its inputs go to tests/TestData/generated
// (git-ignored) and only its golden manifest is committed.

var names = new List<string>();
var check = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--scenario" when i + 1 < args.Length:
            names.Add(args[++i]);
            break;
        case "--check":
            check = true;
            break;
        default:
            await Console.Error.WriteLineAsync($"Unknown argument '{args[i]}'. Usage: [--scenario NAME]... [--check]").ConfigureAwait(false);
            return 2;
    }
}

var repoRoot = ScenarioFileSystem.FindRepositoryRoot(Environment.CurrentDirectory);
var testData = ScenarioFileSystem.TestDataRoot(repoRoot);
var mapping = ScenarioGenerator.LoadContractMapping(repoRoot);
var scenarios = names.Count == 0
    ? ScenarioCatalog.All.Where(s => s.Category == "Default").ToList()
    : names.Select(ScenarioCatalog.Get).ToList();

var drift = 0;
foreach (var scenario in scenarios)
{
    var generated = ScenarioGenerator.Generate(scenario, mapping);
    if (check)
    {
        var problems = ScenarioFileSystem.Drift(generated, testData);
        drift += problems.Count;
        Console.WriteLine($"{scenario.Name}: {(problems.Count == 0 ? "up to date" : $"{problems.Count} difference(s)")}");
        problems.Take(20).ToList().ForEach(p => Console.WriteLine("  " + p));
    }
    else
    {
        ScenarioFileSystem.Write(generated, testData, Path.Combine(testData, "generated"));
        Console.WriteLine($"{scenario.Name}: wrote {generated.Files.Count} file(s)");
    }
}

return drift == 0 ? 0 : 1;
