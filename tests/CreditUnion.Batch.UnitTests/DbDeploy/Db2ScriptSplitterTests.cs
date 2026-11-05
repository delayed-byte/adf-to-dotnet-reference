using CreditUnion.Batch.DbDeploy.Db2;

namespace CreditUnion.Batch.UnitTests.DbDeploy;

public sealed class Db2ScriptSplitterTests
{
    [Fact]
    public void Splits_on_semicolons_and_drops_comments_and_blank_statements()
    {
        const string script = """
            -- header comment; with a semicolon
            CREATE SCHEMA KS;

            /* block; comment */
            CREATE TABLE KS.T (C INT);
            ;
            """;

        Db2ScriptSplitter.Split(script).ShouldBe(["CREATE SCHEMA KS", "CREATE TABLE KS.T (C INT)"]);
    }

    [Fact]
    public void Keeps_semicolons_and_escaped_quotes_inside_literals_and_identifiers()
    {
        const string script = "INSERT INTO KS.X VALUES ('a;b', 'it''s'); SELECT \"odd;name\" FROM KS.X";

        Db2ScriptSplitter.Split(script).ShouldBe(["INSERT INTO KS.X VALUES ('a;b', 'it''s')", "SELECT \"odd;name\" FROM KS.X"]);
    }

    [Fact]
    public void Rejects_an_unterminated_literal()
    {
        Should.Throw<FormatException>(() => Db2ScriptSplitter.Split("SELECT 'oops FROM KS.X;"));
    }

    [Fact]
    public void Every_repository_db2_script_splits_into_statements()
    {
        var folder = Path.Combine(RepoPaths.Root, "db", "keystone-db2", "migrations");
        var files = Directory.GetFiles(folder, "*.sql");

        files.ShouldNotBeEmpty();
        files.ShouldAllBe(f => Db2ScriptSplitter.Split(File.ReadAllText(f)).Count > 0);
    }
}
