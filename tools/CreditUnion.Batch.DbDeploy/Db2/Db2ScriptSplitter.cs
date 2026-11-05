using System.Text;

namespace CreditUnion.Batch.DbDeploy.Db2;

/// <summary>
/// Splits a Db2 script into individual statements. Statements are terminated by ';'.
/// Semicolons inside single-quoted literals, double-quoted identifiers and comments are ignored,
/// and comments are stripped. Compound statements (BEGIN ... END) are not supported by design:
/// the Db2 scripts in this repository are plain DDL and DML.
/// </summary>
internal static class Db2ScriptSplitter
{
    public static IReadOnlyList<string> Split(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var statements = new List<string>();
        var current = new StringBuilder();
        var i = 0;

        while (i < script.Length)
        {
            var c = script[i];
            var next = i + 1 < script.Length ? script[i + 1] : '\0';

            if (c == '-' && next == '-')
            {
                while (i < script.Length && script[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && next == '*')
            {
                var end = script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? script.Length : end + 2;
                current.Append(' ');
                continue;
            }

            if (c is '\'' or '"')
            {
                i = CopyQuoted(script, i, c, current);
                continue;
            }

            if (c == ';')
            {
                AddStatement(statements, current);
                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        AddStatement(statements, current);
        return statements;
    }

    private static int CopyQuoted(string script, int start, char quote, StringBuilder target)
    {
        target.Append(quote);
        var i = start + 1;
        while (i < script.Length)
        {
            target.Append(script[i]);
            if (script[i] == quote)
            {
                // A doubled quote is an escaped quote inside the literal.
                if (i + 1 < script.Length && script[i + 1] == quote)
                {
                    target.Append(quote);
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        throw new FormatException("Unterminated quoted literal in Db2 script.");
    }

    private static void AddStatement(List<string> statements, StringBuilder current)
    {
        var statement = current.ToString().Trim();
        if (statement.Length > 0)
        {
            statements.Add(statement);
        }

        current.Clear();
    }
}
