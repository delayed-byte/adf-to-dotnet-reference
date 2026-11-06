namespace CreditUnion.Batch.Core.Logging;

/// <summary>Masks identifiers for logs and error messages: member, account and card numbers show their last four characters only.</summary>
public static class LogMask
{
    /// <summary><c>****1234</c> for a value of more than four characters; a value of up to four characters is fully masked.</summary>
    public static string LastFour(string? value) =>
        string.IsNullOrEmpty(value) || value.Length <= 4 ? "****" : "****" + value[^4..];
}
