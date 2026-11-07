using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CreditUnion.Batch.UnitTests.Adf;

namespace CreditUnion.Batch.UnitTests.Docs;

/// <summary>
/// Phase 9 gate: the written documentation still matches the repository.
/// <list type="bullet">
/// <item>Every ADF activity (nested ones included) has exactly one row in the playbook's traceability table,
/// with a .NET implementation and the policy the ADF JSON states.</item>
/// <item>Every relative link in README.md and in docs/*.md resolves, including heading anchors in Markdown files.</item>
/// <item>Every <c>./build.ps1 command</c> the README mentions, and every switch used with it, exists in build.ps1.</item>
/// </list>
/// Each test collects all problems and reports them together.
/// </summary>
public sealed partial class DocumentationConsistencyTests
{
    private const string TraceabilityHeading = "Traceability: every ADF activity and its .NET implementation";

    private static readonly Lazy<AdfFactory> LazyFactory = new(() => AdfFactory.Load(RepoPaths.Adf));

    private static string ReadmePath => Path.Combine(RepoPaths.Root, "README.md");

    private static string PlaybookPath => Path.Combine(RepoPaths.Root, "docs", "migration-playbook.md");

    // ---- traceability -------------------------------------------------------------------------

    [Fact]
    public void Every_ADF_activity_has_one_row_in_the_playbook_traceability_table()
    {
        var expected = ExpectedActivities();
        var rows = TraceabilityRows();
        var problems = new List<string>();

        foreach (var key in expected.Keys.Except(rows.Select(r => r.Key), StringComparer.Ordinal))
        {
            problems.Add($"No traceability row for {key}.");
        }

        foreach (var key in rows.Select(r => r.Key).Except(expected.Keys, StringComparer.Ordinal))
        {
            problems.Add($"Traceability row for '{key}' matches no activity in adf/pipeline.");
        }

        foreach (var duplicate in rows.GroupBy(r => r.Key, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"{duplicate.Key} has {duplicate.Count()} rows.");
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
        rows.Count.ShouldBe(expected.Count);
    }

    [Fact]
    public void Traceability_rows_name_an_implementation_an_ADR_and_the_policy_in_the_ADF_JSON()
    {
        var expected = ExpectedActivities();
        var problems = new List<string>();

        foreach (var row in TraceabilityRows())
        {
            if (!expected.TryGetValue(row.Key, out var activity))
            {
                continue;
            }

            if (row.Cells[2] != activity.Type)
            {
                problems.Add($"{row.Key}: type is '{row.Cells[2]}', the ADF JSON says '{activity.Type}'.");
            }

            if (string.IsNullOrWhiteSpace(row.Cells[3]))
            {
                problems.Add($"{row.Key}: no .NET implementation.");
            }

            if (row.Cells[4] != activity.Policy)
            {
                problems.Add($"{row.Key}: policy is '{row.Cells[4]}', the ADF JSON says '{activity.Policy}'.");
            }

            if (!AdrReference().IsMatch(row.Cells[5]))
            {
                problems.Add($"{row.Key}: no ADR reference ('{row.Cells[5]}').");
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Every_ADR_the_traceability_table_cites_exists()
    {
        var adrNumbers = Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "docs", "decisions"), "*.md")
            .Select(f => Path.GetFileName(f)[..4])
            .ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var row in TraceabilityRows())
        {
            foreach (Match match in AdrNumber().Matches(row.Cells[5]))
            {
                if (!adrNumbers.Contains(match.Groups[1].Value))
                {
                    problems.Add($"{row.Key}: ADR {match.Groups[1].Value} does not exist.");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    // ---- links --------------------------------------------------------------------------------

    [Fact]
    public void Every_relative_link_in_README_and_docs_resolves()
    {
        var files = new List<string> { ReadmePath };
        files.AddRange(Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "docs"), "*.md", SearchOption.AllDirectories));
        var problems = new List<string>();

        foreach (var file in files)
        {
            var directory = Path.GetDirectoryName(file)!;
            foreach (var (line, target) in LinksIn(File.ReadAllLines(file)))
            {
                if (IsExternal(target))
                {
                    continue;
                }

                var hash = target.IndexOf('#', StringComparison.Ordinal);
                var pathPart = Uri.UnescapeDataString(hash < 0 ? target : target[..hash]);
                var fragment = hash < 0 ? string.Empty : target[(hash + 1)..];
                var location = $"{Path.GetRelativePath(RepoPaths.Root, file)}:{line}";

                var resolved = pathPart.Length == 0 ? file : Path.GetFullPath(Path.Combine(directory, pathPart));
                if (!File.Exists(resolved) && !Directory.Exists(resolved))
                {
                    problems.Add($"{location}: '{target}' does not exist.");
                    continue;
                }

                if (fragment.Length > 0 && resolved.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    && !LineAnchor().IsMatch(fragment) && !AnchorsOf(resolved).Contains(fragment))
                {
                    problems.Add($"{location}: '{target}' has no heading '{fragment}'.");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    // ---- build.ps1 ----------------------------------------------------------------------------

    [Fact]
    public void Every_build_command_and_switch_the_README_mentions_exists_in_build_ps1()
    {
        var script = File.ReadAllText(Path.Combine(RepoPaths.Root, "build.ps1"));
        var commands = ValidateSetPattern().Match(script) is { Success: true } set
            ? set.Groups[1].Value.Split(',').Select(c => c.Trim().Trim('\'')).ToHashSet(StringComparer.Ordinal)
            : throw new InvalidDataException("build.ps1 has no [ValidateSet(...)].");
        var paramBlock = ParamBlock().Match(script) is { Success: true } block
            ? block.Groups[1].Value
            : throw new InvalidDataException("build.ps1 has no param(...) block.");
        var switches = ParameterPattern().Matches(paramBlock).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();

        var mentions = 0;
        foreach (var (line, text) in File.ReadAllLines(ReadmePath).Select((t, i) => (i + 1, t)))
        {
            foreach (Match match in BuildInvocation().Matches(text))
            {
                mentions++;
                var command = match.Groups[1].Value;
                if (!commands.Contains(command))
                {
                    problems.Add($"README.md:{line}: './build.ps1 {command}' is not a build.ps1 command ({string.Join(", ", commands)}).");
                }

                foreach (Match option in OptionAfterCommand().Matches(match.Groups[2].Value))
                {
                    if (!switches.Contains(option.Groups[1].Value))
                    {
                        problems.Add($"README.md:{line}: '-{option.Groups[1].Value}' is not a build.ps1 parameter.");
                    }
                }
            }
        }

        mentions.ShouldBeGreaterThan(0, "The README should show at least one ./build.ps1 command.");
        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));

        // The reverse direction: a command that exists but the README never explains is a gap.
        var readme = File.ReadAllText(ReadmePath);
        commands.Where(c => !readme.Contains($"./build.ps1 {c}", StringComparison.Ordinal))
            .ShouldBeEmpty("These build.ps1 commands are not documented in the README.");
    }

    // ---- helpers ------------------------------------------------------------------------------

    private sealed record Expected(string Type, string Policy);

    private sealed record Row(string Key, string[] Cells);

    private static Dictionary<string, Expected> ExpectedActivities()
    {
        var result = new Dictionary<string, Expected>(StringComparer.Ordinal);
        foreach (var pipeline in LazyFactory.Value.OfKind("pipeline"))
        {
            foreach (var activity in AdfFactory.Activities(pipeline))
            {
                result[$"{pipeline.Name} / {activity.Name}"] = new Expected(activity.Type, PolicyText(activity.Json));
            }
        }

        return result;
    }

    /// <summary>The policy as the playbook prints it: "{retry} retries, {interval} s, {timeout} min", or "none".</summary>
    private static string PolicyText(JsonObject activity)
    {
        if (activity["policy"] is not JsonObject policy)
        {
            return "none";
        }

        var retry = policy["retry"]!.GetValue<int>();
        var interval = policy["retryIntervalInSeconds"]!.GetValue<int>();
        var timeout = TimeSpan.Parse(policy["timeout"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture, $"{retry} retries, {interval} s, {(int)timeout.TotalMinutes} min");
    }

    private static List<Row> TraceabilityRows()
    {
        var lines = File.ReadAllLines(PlaybookPath);
        var start = Array.FindIndex(lines, l => l.StartsWith("## ", StringComparison.Ordinal) && l.Contains(TraceabilityHeading, StringComparison.Ordinal));
        start.ShouldBeGreaterThanOrEqualTo(0, $"The playbook has no '## ... {TraceabilityHeading}' section.");

        var rows = new List<Row>();
        var inFence = false;
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                break;
            }

            // Activity table: | `PL_x` | `Activity name` | Type | .NET | policy | ADR |
            var cells = SplitRow(line);
            if (cells.Length == 6 && cells[0].StartsWith("`PL_", StringComparison.Ordinal))
            {
                var cleaned = cells.Select(c => c.Trim('`')).ToArray();
                rows.Add(new Row($"{cleaned[0]} / {cleaned[1]}", cleaned));
            }
        }

        return rows;
    }

    private static string[] SplitRow(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('|') || !trimmed.EndsWith('|'))
        {
            return [];
        }

        return trimmed[1..^1].Split('|').Select(c => c.Trim()).ToArray();
    }

    private static bool IsExternal(string target) =>
        target.Contains("://", StringComparison.Ordinal)
        || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
        || target.StartsWith("//", StringComparison.Ordinal);

    /// <summary>Markdown link targets outside fenced code blocks and inline code, with their line numbers.</summary>
    private static IEnumerable<(int Line, string Target)> LinksIn(string[] lines)
    {
        var inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            var withoutCode = InlineCode().Replace(line, string.Empty);
            foreach (Match match in InlineLink().Matches(withoutCode))
            {
                var target = match.Groups[2].Value.Trim();
                if (target.StartsWith('<') && target.EndsWith('>'))
                {
                    target = target[1..^1];
                }

                if (target.Length > 0)
                {
                    yield return (i + 1, target);
                }
            }
        }
    }

    /// <summary>GitHub's heading anchors for a Markdown file.</summary>
    private static HashSet<string> AnchorsOf(string markdownFile)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var inFence = false;
        foreach (var line in File.ReadAllLines(markdownFile))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence || HeadingLine().Match(line) is not { Success: true } heading)
            {
                continue;
            }

            var text = InlineLink().Replace(heading.Groups[1].Value, "$1").Replace("`", string.Empty, StringComparison.Ordinal);
            var slug = SlugForbidden().Replace(text.Trim().ToLowerInvariant(), string.Empty).Replace(' ', '-');
            var n = counts.GetValueOrDefault(slug);
            counts[slug] = n + 1;
            anchors.Add(n == 0 ? slug : $"{slug}-{n}");
        }

        return anchors;
    }

    [GeneratedRegex(@"\[([^\]]*)\]\(([^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex InlineLink();

    [GeneratedRegex("`[^`]*`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"^#{1,6}\s+(.+?)\s*#*\s*$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"[^\p{L}\p{N}\s_-]")]
    private static partial Regex SlugForbidden();

    [GeneratedRegex(@"^L\d+(C\d+)?(-L\d+(C\d+)?)?$")]
    private static partial Regex LineAnchor();

    [GeneratedRegex(@"\[ValidateSet\(([^)]*)\)\]")]
    private static partial Regex ValidateSetPattern();

    [GeneratedRegex(@"\$([A-Za-z]+)")]
    private static partial Regex ParameterPattern();

    [GeneratedRegex(@"(?s)\bparam\((.*?)\r?\n\)")]
    private static partial Regex ParamBlock();

    [GeneratedRegex(@"\./build\.ps1\s+([a-z]+)([^`\r\n]*)")]
    private static partial Regex BuildInvocation();

    [GeneratedRegex(@"(?<=\s)-([A-Za-z]+)")]
    private static partial Regex OptionAfterCommand();

    [GeneratedRegex(@"\b\d{4}\b")]
    private static partial Regex AdrReference();

    [GeneratedRegex(@"\b(\d{4})\b")]
    private static partial Regex AdrNumber();
}
