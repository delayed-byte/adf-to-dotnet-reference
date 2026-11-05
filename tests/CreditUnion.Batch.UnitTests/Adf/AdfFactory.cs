using System.Text.Json;
using System.Text.Json.Nodes;

namespace CreditUnion.Batch.UnitTests.Adf;

/// <summary>One ADF resource file (pipeline, dataset, linked service, ...).</summary>
internal sealed record AdfResource(string Kind, string Name, string FilePath, JsonObject Json)
{
    public JsonObject Properties => Json["properties"] as JsonObject
        ?? throw new InvalidDataException($"{FilePath} has no 'properties' object.");
}

/// <summary>An activity together with the list of activities in its own scope (siblings).</summary>
internal sealed record AdfActivity(string Name, string Type, JsonObject Json, IReadOnlyList<JsonObject> Scope);

/// <summary>Loads the ADF factory definition from <c>adf/</c>, the source of truth for the migration.</summary>
internal sealed class AdfFactory
{
    public static readonly IReadOnlyDictionary<string, string> ReferenceTypeToKind = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PipelineReference"] = "pipeline",
        ["DatasetReference"] = "dataset",
        ["LinkedServiceReference"] = "linkedService",
        ["DataFlowReference"] = "dataflow",
        ["IntegrationRuntimeReference"] = "integrationRuntime",
    };

    public static readonly IReadOnlyList<string> Kinds = ["pipeline", "dataset", "linkedService", "dataflow", "trigger", "integrationRuntime"];

    private readonly Dictionary<string, Dictionary<string, AdfResource>> resources;

    private AdfFactory(Dictionary<string, Dictionary<string, AdfResource>> resources, IReadOnlyList<string> parseErrors)
    {
        this.resources = resources;
        ParseErrors = parseErrors;
    }

    public IReadOnlyList<string> ParseErrors { get; }

    public IEnumerable<AdfResource> All => resources.Values.SelectMany(r => r.Values);

    public IReadOnlyCollection<AdfResource> OfKind(string kind) =>
        resources.TryGetValue(kind, out var byName) ? byName.Values : [];

    public bool Exists(string kind, string name) =>
        resources.TryGetValue(kind, out var byName) && byName.ContainsKey(name);

    public AdfResource Get(string kind, string name) => resources[kind][name];

    public static AdfFactory Load(string adfRoot)
    {
        var resources = new Dictionary<string, Dictionary<string, AdfResource>>(StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (var kind in Kinds)
        {
            var byName = new Dictionary<string, AdfResource>(StringComparer.Ordinal);
            resources[kind] = byName;
            var folder = Path.Combine(adfRoot, kind);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(adfRoot, file);
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow });
                    if (node is not JsonObject obj || obj["name"]?.GetValue<string>() is not { Length: > 0 } name)
                    {
                        errors.Add($"{relative}: missing 'name'.");
                        continue;
                    }

                    if (obj["properties"] is not JsonObject)
                    {
                        errors.Add($"{relative}: missing 'properties' object.");
                        continue;
                    }

                    if (!string.Equals(Path.GetFileNameWithoutExtension(file), name, StringComparison.Ordinal))
                    {
                        errors.Add($"{relative}: file name does not match resource name '{name}'.");
                    }

                    if (!byName.TryAdd(name, new AdfResource(kind, name, relative, obj)))
                    {
                        errors.Add($"{relative}: duplicate {kind} name '{name}'.");
                    }
                }
                catch (JsonException ex)
                {
                    errors.Add($"{relative}: invalid JSON ({ex.Message}).");
                }
            }
        }

        return new AdfFactory(resources, errors);
    }

    /// <summary>All activities of a pipeline, including those nested in ForEach, If Condition, Switch and Until.</summary>
    public static IReadOnlyList<AdfActivity> Activities(AdfResource pipeline)
    {
        var result = new List<AdfActivity>();
        Collect(pipeline.Properties["activities"] as JsonArray, result);
        return result;

        static void Collect(JsonArray? activities, List<AdfActivity> result)
        {
            if (activities is null)
            {
                return;
            }

            var scope = activities.OfType<JsonObject>().ToList();
            foreach (var activity in scope)
            {
                var name = activity["name"]?.GetValue<string>() ?? "<unnamed>";
                var type = activity["type"]?.GetValue<string>() ?? "<untyped>";
                result.Add(new AdfActivity(name, type, activity, scope));

                if (activity["typeProperties"] is JsonObject tp)
                {
                    Collect(tp["activities"] as JsonArray, result);
                    Collect(tp["ifTrueActivities"] as JsonArray, result);
                    Collect(tp["ifFalseActivities"] as JsonArray, result);
                    Collect(tp["defaultActivities"] as JsonArray, result);
                    if (tp["cases"] is JsonArray cases)
                    {
                        foreach (var c in cases.OfType<JsonObject>())
                        {
                            Collect(c["activities"] as JsonArray, result);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Every JSON object below <paramref name="node"/> (depth first), including itself.</summary>
    public static IEnumerable<JsonObject> Objects(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                yield return obj;
                foreach (var (_, child) in obj)
                {
                    foreach (var o in Objects(child))
                    {
                        yield return o;
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    foreach (var o in Objects(child))
                    {
                        yield return o;
                    }
                }

                break;
        }
    }

    /// <summary>Every string value below <paramref name="node"/>.</summary>
    public static IEnumerable<string> Strings(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var s):
                yield return s;
                break;
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    foreach (var s in Strings(child))
                    {
                        yield return s;
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    foreach (var s in Strings(child))
                    {
                        yield return s;
                    }
                }

                break;
        }
    }

    /// <summary>Parameter names of a pipeline or dataset, mapped to whether they have a default value.</summary>
    public static IReadOnlyDictionary<string, bool> Parameters(AdfResource resource) =>
        resource.Properties["parameters"] is JsonObject parameters
            ? parameters.ToDictionary(p => p.Key, p => p.Value is JsonObject def && def.ContainsKey("defaultValue"), StringComparer.Ordinal)
            : new Dictionary<string, bool>(StringComparer.Ordinal);
}
