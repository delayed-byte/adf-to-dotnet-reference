using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CreditUnion.Batch.UnitTests.Adf;

/// <summary>
/// Phase 1 gate: the ADF JSON in <c>adf/</c> parses and is internally consistent. Every
/// pipeline, dataset, linked service, data flow and integration runtime reference resolves;
/// activity dependencies, activity outputs, parameters and variables used in expressions exist;
/// and every stored procedure (and parameter) the factory calls is declared in db/reporting.
/// Each test collects all problems and reports them together.
/// </summary>
public sealed partial class AdfDefinitionValidationTests
{
    private static readonly string[] DataFlowStreamKinds = ["sources", "sinks", "transformations"];
    private static readonly Lazy<AdfFactory> LazyFactory = new(() => AdfFactory.Load(RepoPaths.Adf));
    private static readonly Lazy<IReadOnlyDictionary<string, SqlProcedure>> LazyProcedures =
        new(() => SqlProcedureCatalog.Load(RepoPaths.ReportingProcedures));

    private static AdfFactory Factory => LazyFactory.Value;

    [Fact]
    public void All_json_files_parse_and_are_named_after_their_resource()
    {
        Factory.ParseErrors.ShouldBeEmpty();
        Factory.OfKind("pipeline").Count.ShouldBe(5);
        Factory.OfKind("trigger").Count.ShouldBe(1);
        Factory.OfKind("dataflow").Count.ShouldBe(1);
    }

    [Fact]
    public void Every_resource_reference_resolves()
    {
        var problems = new List<string>();
        foreach (var resource in Factory.All)
        {
            foreach (var reference in AdfFactory.Objects(resource.Json))
            {
                if (reference["referenceName"] is not JsonValue nameNode || reference["type"]?.GetValue<string>() is not { } type)
                {
                    continue;
                }

                var name = nameNode.GetValue<string>();
                if (!AdfFactory.ReferenceTypeToKind.TryGetValue(type, out var kind))
                {
                    problems.Add($"{resource.FilePath}: unknown reference type '{type}' for '{name}'.");
                }
                else if (!Factory.Exists(kind, name))
                {
                    problems.Add($"{resource.FilePath}: {type} '{name}' does not exist under adf/{kind}/.");
                }
            }
        }

        problems.ShouldBeEmpty();
    }

    [Fact]
    public void Activity_names_are_unique_and_dependencies_resolve_within_scope()
    {
        var problems = new List<string>();
        foreach (var pipeline in Factory.OfKind("pipeline"))
        {
            var activities = AdfFactory.Activities(pipeline);
            problems.AddRange(activities.GroupBy(a => a.Name, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => $"{pipeline.Name}: activity name '{g.Key}' is used {g.Count()} times."));

            foreach (var activity in activities)
            {
                var siblings = activity.Scope.Select(a => a["name"]?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                foreach (var dependency in (activity.Json["dependsOn"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var target = dependency["activity"]?.GetValue<string>();
                    if (target is null || !siblings.Contains(target))
                    {
                        problems.Add($"{pipeline.Name}/{activity.Name}: depends on '{target}', which is not in the same scope.");
                    }
                }
            }
        }

        problems.ShouldBeEmpty();
    }

    [Fact]
    public void Expressions_reference_existing_activities_parameters_and_variables()
    {
        var problems = new List<string>();
        foreach (var pipeline in Factory.OfKind("pipeline"))
        {
            var activityNames = AdfFactory.Activities(pipeline).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
            var parameters = AdfFactory.Parameters(pipeline);
            var variables = (pipeline.Properties["variables"] as JsonObject)?.Select(v => v.Key).ToHashSet(StringComparer.Ordinal) ?? [];

            foreach (var text in AdfFactory.Strings(pipeline.Properties["activities"]))
            {
                foreach (Match m in ActivityReference().Matches(text))
                {
                    if (!activityNames.Contains(m.Groups[1].Value))
                    {
                        problems.Add($"{pipeline.Name}: expression references unknown activity '{m.Groups[1].Value}'.");
                    }
                }

                foreach (Match m in PipelineParameterReference().Matches(text))
                {
                    if (!parameters.ContainsKey(m.Groups[1].Value))
                    {
                        problems.Add($"{pipeline.Name}: expression references unknown parameter '{m.Groups[1].Value}'.");
                    }
                }

                foreach (Match m in VariableReference().Matches(text))
                {
                    if (!variables.Contains(m.Groups[1].Value))
                    {
                        problems.Add($"{pipeline.Name}: expression references unknown variable '{m.Groups[1].Value}'.");
                    }
                }
            }

            foreach (var setVariable in AdfFactory.Activities(pipeline).Where(a => a.Type == "SetVariable"))
            {
                var variableName = setVariable.Json["typeProperties"]?["variableName"]?.GetValue<string>();
                if (variableName is null || !variables.Contains(variableName))
                {
                    problems.Add($"{pipeline.Name}/{setVariable.Name}: sets undeclared variable '{variableName}'.");
                }
            }
        }

        foreach (var dataset in Factory.OfKind("dataset"))
        {
            var parameters = AdfFactory.Parameters(dataset);
            foreach (var text in AdfFactory.Strings(dataset.Properties["typeProperties"]))
            {
                foreach (Match m in DatasetParameterReference().Matches(text))
                {
                    if (!parameters.ContainsKey(m.Groups[1].Value))
                    {
                        problems.Add($"{dataset.Name}: expression references unknown dataset parameter '{m.Groups[1].Value}'.");
                    }
                }
            }
        }

        problems.ShouldBeEmpty();
    }

    [Fact]
    public void Pipeline_and_dataset_parameters_passed_by_callers_match_their_declarations()
    {
        var problems = new List<string>();

        // Execute Pipeline activities and the trigger must pass exactly declared parameters,
        // and every parameter without a default.
        var pipelineCalls = Factory.OfKind("pipeline")
            .SelectMany(p => AdfFactory.Activities(p)
                .Where(a => a.Type == "ExecutePipeline")
                .Select(a => (Caller: $"{p.Name}/{a.Name}", Reference: a.Json["typeProperties"]?["pipeline"] as JsonObject, Parameters: a.Json["typeProperties"]?["parameters"] as JsonObject)))
            .Concat(Factory.OfKind("trigger")
                .SelectMany(t => (t.Properties["pipelines"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(p => (Caller: t.Name, Reference: p["pipelineReference"] as JsonObject, Parameters: p["parameters"] as JsonObject))));

        foreach (var (caller, reference, passed) in pipelineCalls)
        {
            var target = reference?["referenceName"]?.GetValue<string>();
            if (target is null || !Factory.Exists("pipeline", target))
            {
                continue; // reported by Every_resource_reference_resolves
            }

            problems.AddRange(CheckArguments(caller, $"pipeline {target}", AdfFactory.Parameters(Factory.Get("pipeline", target)), passed));
        }

        // Dataset references outside data flows must pass declared dataset parameters (data flow
        // datasets receive theirs from the Execute Data Flow activity's datasetParameters).
        foreach (var resource in Factory.All.Where(r => r.Kind != "dataflow"))
        {
            foreach (var reference in AdfFactory.Objects(resource.Json).Where(o => o["type"]?.GetValue<string>() == "DatasetReference"))
            {
                var target = reference["referenceName"]?.GetValue<string>();
                if (target is null || !Factory.Exists("dataset", target))
                {
                    continue;
                }

                problems.AddRange(CheckArguments(resource.Name, $"dataset {target}", AdfFactory.Parameters(Factory.Get("dataset", target)), reference["parameters"] as JsonObject));
            }
        }

        problems.ShouldBeEmpty();
    }

    [Fact]
    public void Execute_data_flow_activities_match_the_data_flow_definition()
    {
        var problems = new List<string>();
        foreach (var pipeline in Factory.OfKind("pipeline"))
        {
            foreach (var activity in AdfFactory.Activities(pipeline).Where(a => a.Type == "ExecuteDataFlow"))
            {
                var dataflowRef = activity.Json["typeProperties"]?["dataflow"] as JsonObject;
                var name = dataflowRef?["referenceName"]?.GetValue<string>();
                if (name is null || !Factory.Exists("dataflow", name))
                {
                    continue;
                }

                var typeProperties = Factory.Get("dataflow", name).Properties["typeProperties"]!;
                var endpoints = (typeProperties["sources"] as JsonArray ?? []).Concat(typeProperties["sinks"] as JsonArray ?? [])
                    .Select(e => e?["name"]?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal);
                var datasetParameters = (dataflowRef!["datasetParameters"] as JsonObject)?.Select(p => p.Key).ToHashSet(StringComparer.Ordinal) ?? [];
                if (!datasetParameters.SetEquals(endpoints))
                {
                    problems.Add($"{pipeline.Name}/{activity.Name}: datasetParameters [{string.Join(", ", datasetParameters.Order(StringComparer.Ordinal))}] do not match the sources and sinks of {name} [{string.Join(", ", endpoints.Order(StringComparer.Ordinal))}].");
                }

                var script = string.Join('\n', (typeProperties["scriptLines"] as JsonArray ?? []).Select(l => l?.GetValue<string>()));
                var block = ScriptParametersBlock().Match(script);
                var declared = block.Success
                    ? ScriptParameterLine().Matches(block.Groups[1].Value).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal)
                    : [];
                var passed = (dataflowRef["parameters"] as JsonObject)?.Select(p => p.Key).ToHashSet(StringComparer.Ordinal) ?? [];
                if (!passed.SetEquals(declared))
                {
                    problems.Add($"{pipeline.Name}/{activity.Name}: passes parameters [{string.Join(", ", passed)}] but {name} declares [{string.Join(", ", declared)}].");
                }
            }
        }

        problems.ShouldBeEmpty();
    }

    [Fact]
    public void Data_flow_script_streams_match_declared_sources_sinks_and_transformations()
    {
        var problems = new List<string>();
        foreach (var dataflow in Factory.OfKind("dataflow"))
        {
            var typeProperties = dataflow.Properties["typeProperties"]!;
            var declared = DataFlowStreamKinds
                .SelectMany(k => typeProperties[k] as JsonArray ?? [])
                .Select(e => e?["name"]?.GetValue<string>()).OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            var script = string.Join('\n', (typeProperties["scriptLines"] as JsonArray ?? []).Select(l => l?.GetValue<string>()));
            var streams = ScriptStream().Matches(script).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

            if (!streams.SetEquals(declared))
            {
                problems.Add($"{dataflow.Name}: script streams [{string.Join(", ", streams.Order(StringComparer.Ordinal))}] differ from declared [{string.Join(", ", declared.Order(StringComparer.Ordinal))}].");
            }
        }

        problems.ShouldBeEmpty();
    }

    [Fact]
    public void Every_stored_procedure_and_parameter_used_by_the_factory_is_declared_in_db_reporting()
    {
        var procedures = LazyProcedures.Value;
        var problems = new List<string>();

        foreach (var pipeline in Factory.OfKind("pipeline"))
        {
            foreach (var activity in AdfFactory.Activities(pipeline))
            {
                foreach (var (procedureName, parameters) in ProcedureCalls(activity))
                {
                    var name = SqlProcedureCatalog.Normalise(procedureName);
                    if (!procedures.TryGetValue(name, out var procedure))
                    {
                        problems.Add($"{pipeline.Name}/{activity.Name}: stored procedure {name} is not declared in db/reporting/procedures.");
                        continue;
                    }

                    problems.AddRange(CheckArguments($"{pipeline.Name}/{activity.Name}", $"procedure {name}", procedure.Parameters, parameters));
                }
            }
        }

        problems.ShouldBeEmpty();
    }

    [Fact]
    public void Factory_covers_every_adf_feature_in_scope()
    {
        var activityTypes = Factory.OfKind("pipeline").SelectMany(AdfFactory.Activities).Select(a => a.Type).ToHashSet(StringComparer.Ordinal);
        string[] required = ["Copy", "Lookup", "GetMetadata", "ForEach", "IfCondition", "SqlServerStoredProcedure", "WebActivity", "ExecutePipeline", "ExecuteDataFlow"];
        required.Where(t => !activityTypes.Contains(t)).ShouldBeEmpty("activity types missing from adf/pipeline");

        var copySources = Factory.OfKind("pipeline").SelectMany(AdfFactory.Activities)
            .Where(a => a.Type == "Copy")
            .Select(a => a.Json["typeProperties"]?["source"]?["type"]?.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        copySources.ShouldContain("Db2Source");
        copySources.ShouldContain("DelimitedTextSource");

        Factory.OfKind("trigger").Select(t => t.Properties["type"]?.GetValue<string>()).ShouldContain("ScheduleTrigger");
        Factory.OfKind("pipeline").ShouldAllBe(p => AdfFactory.Parameters(p).ContainsKey("businessDate"));
    }

    [Fact]
    public void Parallelism_retry_and_schedule_settings_match_the_specification()
    {
        BatchCount("PL_ExtractKeyStone", "For Each Source Table").ShouldBe(3);
        BatchCount("PL_IngestCardSettlement", "For Each Settlement File").ShouldBe(4);

        // AT-04 and AT-11 retry twice before succeeding, so these activities need at least 2 retries.
        Retry("PL_ExtractKeyStone", "Copy Db2 To Staging").ShouldBeGreaterThanOrEqualTo(2);
        Retry("PL_MapAndAggregate", "Get GL Mapping From KeyBridge").ShouldBeGreaterThanOrEqualTo(2);

        // Every activity with a policy has a timeout, and ADF's 30 s minimum retry interval.
        var problems = Factory.OfKind("pipeline").SelectMany(p => AdfFactory.Activities(p).Select(a => (Pipeline: p, Activity: a)))
            .Where(x => x.Activity.Json["policy"] is JsonObject)
            .Where(x => x.Activity.Json["policy"]!["timeout"] is null || x.Activity.Json["policy"]!["retryIntervalInSeconds"]?.GetValue<int>() < 30)
            .Select(x => $"{x.Pipeline.Name}/{x.Activity.Name}")
            .ToList();
        problems.ShouldBeEmpty("activities whose policy lacks a timeout or retries faster than 30 s");

        var recurrence = Factory.Get("trigger", "TR_Nightly_0300_Pacific").Properties["typeProperties"]!["recurrence"]!;
        recurrence["frequency"]!.GetValue<string>().ShouldBe("Day");
        recurrence["timeZone"]!.GetValue<string>().ShouldBe("Pacific Standard Time");
        recurrence["schedule"]!["hours"]!.AsArray().Select(h => h!.GetValue<int>()).ShouldBe([3]);
        recurrence["schedule"]!["minutes"]!.AsArray().Select(m => m!.GetValue<int>()).ShouldBe([0]);
    }

    private static int BatchCount(string pipeline, string activity) =>
        Activity(pipeline, activity)["typeProperties"]!["batchCount"]!.GetValue<int>();

    private static int Retry(string pipeline, string activity) =>
        Activity(pipeline, activity)["policy"]!["retry"]!.GetValue<int>();

    private static JsonObject Activity(string pipeline, string activity) =>
        AdfFactory.Activities(Factory.Get("pipeline", pipeline)).Single(a => a.Name == activity).Json;

    private static IEnumerable<(string Name, JsonObject? Parameters)> ProcedureCalls(AdfActivity activity)
    {
        var typeProperties = activity.Json["typeProperties"] as JsonObject;
        if (typeProperties?["storedProcedureName"]?.GetValue<string>() is { } spName)
        {
            yield return (spName, typeProperties["storedProcedureParameters"] as JsonObject);
        }

        if (typeProperties?["source"] is JsonObject source && source["sqlReaderStoredProcedureName"]?.GetValue<string>() is { } readerName)
        {
            yield return (readerName, source["storedProcedureParameters"] as JsonObject);
        }
    }

    private static IEnumerable<string> CheckArguments(string caller, string target, IReadOnlyDictionary<string, bool> declared, JsonObject? passed)
    {
        var passedNames = passed?.Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        foreach (var name in passedNames.Where(n => !declared.ContainsKey(n)))
        {
            yield return $"{caller}: passes '{name}', which {target} does not declare.";
        }

        foreach (var (name, hasDefault) in declared)
        {
            if (!hasDefault && !passedNames.Contains(name))
            {
                yield return $"{caller}: does not pass required '{name}' to {target}.";
            }
        }
    }

    [GeneratedRegex(@"activity\('([^']+)'\)")]
    private static partial Regex ActivityReference();

    [GeneratedRegex(@"pipeline\(\)\.parameters\.(\w+)")]
    private static partial Regex PipelineParameterReference();

    [GeneratedRegex(@"variables\('([^']+)'\)")]
    private static partial Regex VariableReference();

    [GeneratedRegex(@"dataset\(\)\.(\w+)")]
    private static partial Regex DatasetParameterReference();

    [GeneratedRegex(@"~>\s*(\w+)")]
    private static partial Regex ScriptStream();

    // Data flow parameters are the "name as type" lines inside the leading parameters{ ... } block.
    [GeneratedRegex(@"^\s*parameters\{(.*?)\}", RegexOptions.Singleline)]
    private static partial Regex ScriptParametersBlock();

    [GeneratedRegex(@"^\s*(\w+)\s+as\s+\w+", RegexOptions.Multiline)]
    private static partial Regex ScriptParameterLine();
}
