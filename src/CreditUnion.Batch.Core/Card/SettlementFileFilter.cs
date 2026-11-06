using System.Globalization;

namespace CreditUnion.Batch.Core.Card;

/// <summary>
/// ADF activity "Filter Settlement Files":
/// <c>and(equals(item().type, 'File'), and(startsWith(item().name, 'settlement_'), endsWith(item().name, '.csv')))</c>.
/// ADF's string functions compare case-insensitively, so <c>SETTLEMENT_003.CSV</c> matches (ADR 0007). Pure
/// and free of I/O, so an orchestrator may call it.
/// </summary>
public static class SettlementFileFilter
{
    public const string Prefix = "settlement_";
    public const string Suffix = ".csv";

    public static bool IsSettlementFile(LandingItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return string.Equals(item.Type, LandingItem.FileType, StringComparison.OrdinalIgnoreCase)
            && item.Name is not null
            && item.Name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && item.Name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The matching items in their original order. <c>coalesce(childItems, json('[]'))</c>: a null list is an empty one.</summary>
    public static IReadOnlyList<LandingItem> Select(IEnumerable<LandingItem>? items) => [.. (items ?? []).Where(IsSettlementFile)];
}

/// <summary>The Notes of "Log Card Ingest Succeeded", built as the ADF expression builds them.</summary>
public static class CardIngestNotes
{
    public const string NoFiles = "No settlement files found in the landing folder.";

    public static string ForFileCount(int filteredItemsCount) => filteredItemsCount == 0
        ? NoFiles
        : string.Create(CultureInfo.InvariantCulture, $"{filteredItemsCount} settlement file(s) processed; see ops.CardFileLog.");
}
