using System.Security.Cryptography;

namespace CreditUnion.Batch.TestHarness;

/// <summary>
/// Credentials generated for one test run (PRD: container passwords generated per test run). They
/// are never written to disk or logged; AT-17 uses <see cref="All"/> to prove no log line contains one.
/// </summary>
public sealed record TestSecrets(
    string Db2AdminPassword,
    string Db2ExtractPassword,
    string SqlAdminPassword,
    string SqlWriterPassword,
    string SqlReaderPassword,
    string KeyBridgePassword,
    string AlertWebhookToken)
{
    public IReadOnlyList<string> All => [Db2AdminPassword, Db2ExtractPassword, SqlAdminPassword, SqlWriterPassword, SqlReaderPassword, KeyBridgePassword, AlertWebhookToken];

    public static TestSecrets Generate() => new(New(), New(), New(), New(), New(), New(), New());

    /// <summary>Meets SQL Server and Db2 complexity rules and contains no connection-string metacharacters.</summary>
    public static string New()
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        return "Aa1-" + string.Concat(Convert.ToBase64String(bytes).Select(c => char.IsAsciiLetterOrDigit(c) ? c : 'x'));
    }
}
