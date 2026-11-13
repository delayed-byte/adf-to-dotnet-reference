using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.UnitTests.Adf;

namespace CreditUnion.Batch.UnitTests.Infra;

/// <summary>
/// ADR 0020: the Azure template still matches the code and the ADF JSON. The template is read as text, so the suite
/// does not need the Bicep CLI; <c>./build.ps1 infra</c> compiles and lints it.
/// <list type="bullet">
/// <item>Every app setting for an options section names a real property, and every property without a default is set.</item>
/// <item>Key Vault references use the ADF secret names, except the two stopgaps ADR 0020 names.</item>
/// <item><c>WEBSITE_TIME_ZONE</c> is the ADF trigger's time zone (ADR 0016).</item>
/// <item><c>host.json</c>'s <c>functionTimeout</c> is longer than every ADF activity timeout.</item>
/// </list>
/// </summary>
public sealed partial class InfrastructureTemplateTests
{
    private static readonly Type[] OptionsTypes =
    [
        typeof(KeyStoneDb2Options),
        typeof(ReportingSqlOptions),
        typeof(BlobStorageOptions),
        typeof(KeyBridgeOptions),
        typeof(ReconAlertWebhookOptions),
    ];

    /// <summary>Template secrets that are not ADF secret names, and the ADF secret the template does not use (ADR 0019, ADR 0020).</summary>
    private static readonly HashSet<string> StopgapSecrets = new(StringComparer.Ordinal)
    {
        "keystone-db2-extract-connection-string",
        "batch-blob-storage-connection-string",
    };

    private const string UnusedAdfSecret = "keystone-db2-extract-password";

    private static readonly Lazy<string> LazyTemplate = new(() => File.ReadAllText(Path.Combine(RepoPaths.Root, "infra", "main.bicep")));

    private static readonly Lazy<AdfFactory> LazyFactory = new(() => AdfFactory.Load(RepoPaths.Adf));

    private static string HostJsonPath => Path.Combine(RepoPaths.Root, "src", "CreditUnion.Batch.Functions", "host.json");

    [Fact]
    public void Every_options_app_setting_names_a_real_property()
    {
        var sections = OptionsTypes.ToDictionary(SectionName, t => t, StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var name in AppSettings().Keys.Where(k => k.Contains("__", StringComparison.Ordinal)))
        {
            var parts = name.Split("__");
            if (!sections.TryGetValue(parts[0], out var type))
            {
                continue;
            }

            if (parts.Length != 2 || type.GetProperty(parts[1], BindingFlags.Public | BindingFlags.Instance) is null)
            {
                problems.Add($"App setting {name} matches no property of {type.Name}.");
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Every_options_property_without_a_default_is_set()
    {
        var settings = AppSettings();
        var problems = new List<string>();

        foreach (var type in OptionsTypes)
        {
            var instance = Activator.CreateInstance(type)!;
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var value = property.GetValue(instance);
                var hasDefault = value switch
                {
                    null => false,
                    string s => s.Length > 0,
                    _ => true,
                };

                var name = $"{SectionName(type)}__{property.Name}";
                if (!hasDefault && !settings.ContainsKey(name))
                {
                    problems.Add($"{type.Name}.{property.Name} has no default and infra/main.bicep sets no {name}.");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Key_Vault_references_use_the_ADF_secret_names()
    {
        var adfSecrets = AdfSecretNames();
        var templateSecrets = AppSettings().Values.Select(v => v.SecretName).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var secret in adfSecrets.Where(s => s != UnusedAdfSecret).Except(templateSecrets, StringComparer.Ordinal))
        {
            problems.Add($"The ADF JSON reads secret '{secret}', but no app setting references it.");
        }

        foreach (var secret in templateSecrets.Except(adfSecrets, StringComparer.Ordinal).Except(StopgapSecrets, StringComparer.Ordinal))
        {
            problems.Add($"App settings reference secret '{secret}', which the ADF JSON does not use and ADR 0020 does not name.");
        }

        adfSecrets.ShouldContain(UnusedAdfSecret, "ADR 0019: the Db2 password secret the template replaces with a connection-string secret");
        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Secrets_are_Key_Vault_references_not_literals()
    {
        foreach (var name in new[] { "KeyStoneDb2__ConnectionString", "ReportingSql__ConnectionString", "BlobStorage__ConnectionString", "KeyBridge__Password", "ReconAlertWebhook__Url" })
        {
            AppSettings()[name].SecretName.ShouldNotBeNull($"{name} must be a Key Vault reference");
        }
    }

    [Fact]
    public void WEBSITE_TIME_ZONE_is_the_ADF_trigger_time_zone()
    {
        var trigger = LazyFactory.Value.Get("trigger", "TR_Nightly_0300_Pacific");
        var adfZone = trigger.Properties["typeProperties"]!["recurrence"]!["timeZone"]!.GetValue<string>();

        AppSettings()["WEBSITE_TIME_ZONE"].Literal.ShouldBe(adfZone, "ADR 0016: the Windows ID on a Windows plan");
    }

    [Fact]
    public void Host_functionTimeout_is_longer_than_every_ADF_activity_timeout()
    {
        var host = JsonNode.Parse(File.ReadAllText(HostJsonPath))!;
        var functionTimeout = TimeSpan.Parse(host["functionTimeout"]!.GetValue<string>(), CultureInfo.InvariantCulture);

        var longest = LazyFactory.Value.OfKind("pipeline")
            .SelectMany(AdfFactory.Activities)
            .Select(a => a.Json["policy"]?["timeout"]?.GetValue<string>())
            .OfType<string>()
            .Select(t => TimeSpan.Parse(t, CultureInfo.InvariantCulture))
            .Max();

        longest.ShouldBe(TimeSpan.FromHours(2), "the longest ADF timeout today (Aggregate Daily GL Totals)");
        functionTimeout.ShouldBeGreaterThan(longest, "the per-attempt timeout in code must fire before the host kills the function (ADR 0020)");
    }

    [Fact]
    public void Task_hub_override_keeps_the_host_json_hub_name_as_prefix()
    {
        var host = JsonNode.Parse(File.ReadAllText(HostJsonPath))!;
        var hubName = host["extensions"]!["durableTask"]!["hubName"]!.GetValue<string>();

        AppSettings().ShouldContainKey("AzureFunctionsJobHost__extensions__durableTask__hubName");
        TaskHubVariable().Match(LazyTemplate.Value).Groups[1].Value.ShouldBe(hubName);
    }

    // ---- parsing ------------------------------------------------------------------------------

    private static string SectionName(Type optionsType) =>
        (string)optionsType.GetField("SectionName", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    private sealed record SettingValue(string? Literal, string? SecretName);

    private static Dictionary<string, SettingValue> AppSettings()
    {
        var template = LazyTemplate.Value;
        var parameterDefaults = ParameterDefault().Matches(template)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
        var result = new Dictionary<string, SettingValue>(StringComparer.Ordinal);

        foreach (Match match in AppSetting().Matches(template))
        {
            var name = match.Groups[1].Value;
            var value = match.Groups[2].Value.Trim();
            SettingValue setting;
            if (KeyVaultReferenceCall().Match(value) is { Success: true } reference)
            {
                var argument = reference.Groups[1].Value.Trim();
                var secret = argument.StartsWith('\'')
                    ? argument.Trim('\'')
                    : (parameterDefaults.TryGetValue(argument, out var d) ? d : TemplateVariable(template, argument));
                setting = new SettingValue(null, secret);
            }
            else
            {
                setting = new SettingValue(value.StartsWith('\'') && value.EndsWith('\'') ? value.Trim('\'') : null, null);
            }

            result.TryAdd(name, setting).ShouldBeTrue($"infra/main.bicep sets {name} twice");
        }

        result.ShouldNotBeEmpty("no app settings found in infra/main.bicep");
        return result;
    }

    private static string TemplateVariable(string template, string name)
    {
        var match = Regex.Match(template, $@"^var {Regex.Escape(name)} = '([^'$]+)'", RegexOptions.Multiline, TimeSpan.FromSeconds(1));
        match.Success.ShouldBeTrue($"infra/main.bicep: '{name}' is neither a parameter with a default nor a literal variable");
        return match.Groups[1].Value;
    }

    private static HashSet<string> AdfSecretNames()
    {
        var all = LazyFactory.Value.All.Select(r => r.Json).ToList();
        var names = all.SelectMany(AdfFactory.Objects)
            .Select(o => o["secretName"])
            .OfType<JsonValue>()
            .Select(v => v.GetValue<string>());
        var fromUrls = all.SelectMany(AdfFactory.Strings)
            .SelectMany(s => VaultSecretUrl().Matches(s).Select(m => m.Groups[1].Value));
        return names.Concat(fromUrls).ToHashSet(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"^\s*\{\s*name:\s*'([^']+)',\s*value:\s*(.+?)\s*\}", RegexOptions.Multiline, 1000)]
    private static partial Regex AppSetting();

    [GeneratedRegex(@"^keyVaultReference\(\s*keyVaultName\s*,\s*([^)]+)\)$", RegexOptions.None, 1000)]
    private static partial Regex KeyVaultReferenceCall();

    [GeneratedRegex(@"^param (\w+) string = '([^']*)'", RegexOptions.Multiline, 1000)]
    private static partial Regex ParameterDefault();

    [GeneratedRegex(@"^var taskHubName = '([A-Za-z]+)\$\{", RegexOptions.Multiline, 1000)]
    private static partial Regex TaskHubVariable();

    [GeneratedRegex(@"\.vault\.azure\.net/secrets/([A-Za-z0-9-]+)", RegexOptions.None, 1000)]
    private static partial Regex VaultSecretUrl();
}
