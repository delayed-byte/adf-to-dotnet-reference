using System.Globalization;
using System.Text.Json;

namespace CreditUnion.Batch.TestHarness.Golden;

/// <summary>
/// Semantic JSON comparison for golden documents (ADR 0006): numbers compare as decimals (so 0.01 and
/// 0.010 are equal and no float is involved), strings ordinally, an absent property equals null (ADF's
/// JSON sink may omit nulls), and arrays in order unless the caller compares them as sets.
/// </summary>
public static class JsonEquivalence
{
    /// <param name="unorderedArrays">Compare arrays as multisets. Error manifests need this: the procedure's ORDER BY can tie.</param>
    public static IReadOnlyList<string> Differences(string expectedJson, string actualJson, int maxReported = 20, bool unorderedArrays = false)
    {
        using var expected = JsonDocument.Parse(expectedJson);
        using var actual = JsonDocument.Parse(actualJson);
        var differences = new List<string>();
        Compare("$", expected.RootElement, actual.RootElement, differences, unorderedArrays);
        return [.. differences.Take(maxReported)];
    }

    /// <summary>Order-independent text of a value: object keys sorted, nulls dropped, numbers as decimals.</summary>
    public static string Canonical(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().Where(p => !IsNull(p.Value)).OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Canonical)) + "]",
        JsonValueKind.Number => element.GetDecimal().ToString("G29", CultureInfo.InvariantCulture),
        JsonValueKind.Null or JsonValueKind.Undefined => "null",
        _ => element.GetRawText(),
    };

    private static void Compare(string path, JsonElement e, JsonElement a, List<string> differences, bool unorderedArrays)
    {
        if (IsNull(e) && IsNull(a))
        {
            return;
        }

        if (e.ValueKind != a.ValueKind)
        {
            differences.Add($"{path}: expected {e.ValueKind} {Show(e)}, actual {a.ValueKind} {Show(a)}");
            return;
        }

        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var names = e.EnumerateObject().Select(p => p.Name).Union(a.EnumerateObject().Select(p => p.Name), StringComparer.Ordinal).Order(StringComparer.Ordinal);
                foreach (var name in names)
                {
                    var hasE = e.TryGetProperty(name, out var ev);
                    var hasA = a.TryGetProperty(name, out var av);
                    if (hasE && hasA)
                    {
                        Compare($"{path}.{name}", ev, av, differences, unorderedArrays);
                    }
                    else if (!(hasE ? IsNull(ev) : IsNull(av)))
                    {
                        differences.Add($"{path}.{name}: {(hasE ? "missing in actual" : "unexpected in actual")} ({Show(hasE ? ev : av)})");
                    }
                }

                break;
            case JsonValueKind.Array:
                var ea = e.EnumerateArray().ToList();
                var aa = a.EnumerateArray().ToList();
                if (ea.Count != aa.Count)
                {
                    differences.Add($"{path}: expected {ea.Count} element(s), actual {aa.Count}");
                }

                if (unorderedArrays)
                {
                    ea = [.. ea.OrderBy(Canonical, StringComparer.Ordinal)];
                    aa = [.. aa.OrderBy(Canonical, StringComparer.Ordinal)];
                }

                for (var i = 0; i < Math.Min(ea.Count, aa.Count); i++)
                {
                    Compare($"{path}[{i}]", ea[i], aa[i], differences, unorderedArrays);
                }

                break;
            case JsonValueKind.Number:
                if (e.GetDecimal() != a.GetDecimal())
                {
                    differences.Add($"{path}: expected {e.GetRawText()}, actual {a.GetRawText()}");
                }

                break;
            case JsonValueKind.String:
                if (!string.Equals(e.GetString(), a.GetString(), StringComparison.Ordinal))
                {
                    differences.Add($"{path}: expected {Show(e)}, actual {Show(a)}");
                }

                break;
            default:
                if (e.ValueKind != a.ValueKind)
                {
                    differences.Add($"{path}: expected {Show(e)}, actual {Show(a)}");
                }

                break;
        }
    }

    private static bool IsNull(JsonElement element) => element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    private static string Show(JsonElement element)
    {
        var text = element.GetRawText();
        return text.Length > 80 ? string.Create(CultureInfo.InvariantCulture, $"{text[..80]}...") : text;
    }
}
