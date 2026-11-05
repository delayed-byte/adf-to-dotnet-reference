using System.Text.RegularExpressions;

namespace CreditUnion.Batch.UnitTests.Adf;

/// <summary>A stored procedure declared in <c>db/reporting/procedures</c>, with its parameters.</summary>
internal sealed record SqlProcedure(string Name, IReadOnlyDictionary<string, bool> Parameters);

/// <summary>
/// Reads the stored procedure declarations (name and parameters, and whether each parameter
/// has a default) from the reporting database scripts, so ADF references can be checked.
/// </summary>
internal static partial class SqlProcedureCatalog
{
    public static IReadOnlyDictionary<string, SqlProcedure> Load(string proceduresFolder)
    {
        var result = new Dictionary<string, SqlProcedure>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(proceduresFolder, "*.sql"))
        {
            foreach (Match match in ProcedureHeader().Matches(File.ReadAllText(file)))
            {
                var name = $"{match.Groups["schema"].Value}.{match.Groups["name"].Value}";
                var parameters = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var segment in match.Groups["params"].Value.Split('@', StringSplitOptions.RemoveEmptyEntries))
                {
                    var paramName = ParameterName().Match(segment);
                    if (paramName.Success)
                    {
                        parameters[paramName.Value] = segment.Contains('=', StringComparison.Ordinal);
                    }
                }

                result[name] = new SqlProcedure(name, parameters);
            }
        }

        return result;
    }

    /// <summary>Normalises "[ops].[usp_X]" or "ops.usp_X" to "ops.usp_X".</summary>
    public static string Normalise(string procedureName) =>
        procedureName.Replace("[", string.Empty, StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal);

    [GeneratedRegex(@"CREATE\s+OR\s+ALTER\s+PROCEDURE\s+\[?(?<schema>\w+)\]?\.\[?(?<name>\w+)\]?(?<params>.*?)^\s*AS\s*$", RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ProcedureHeader();

    [GeneratedRegex(@"^\w+")]
    private static partial Regex ParameterName();
}
