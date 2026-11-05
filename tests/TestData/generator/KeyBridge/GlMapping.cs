using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CreditUnion.Batch.TestData.KeyBridge;

/// <summary>One row of the KeyBridge GL mapping (contracts/keybridge/README.md).</summary>
public sealed record GlMappingRow(string TransactionCode, string GlAccountNumber, string Side, decimal AllocationRatio, string? Description);

/// <summary>
/// The KeyBridge <c>GET /v1/reference/transaction-codes/gl-mappings</c> response. Parsed with exact
/// decimals; serialised in a fixed property order so the stub body and golden files are stable.
/// </summary>
public sealed record GlMappingResponse(string Version, IReadOnlyList<GlMappingRow> Mappings)
{
    public static GlMappingResponse Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var rows = root.GetProperty("mappings").EnumerateArray().Select(m => new GlMappingRow(
            m.GetProperty("transactionCode").GetString()!,
            m.GetProperty("glAccountNumber").GetString()!,
            m.GetProperty("side").GetString()!,
            m.GetProperty("allocationRatio").GetDecimal(),
            m.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null)).ToList();
        return new GlMappingResponse(root.GetProperty("version").GetString()!, rows);
    }

    public string ToJson()
    {
        var mappings = new JsonArray();
        foreach (var m in Mappings)
        {
            var row = new JsonObject
            {
                ["transactionCode"] = m.TransactionCode,
                ["glAccountNumber"] = m.GlAccountNumber,
                ["side"] = m.Side,
                ["allocationRatio"] = JsonValue.Create(m.AllocationRatio),
            };
            if (m.Description is not null)
            {
                row["description"] = m.Description;
            }

            mappings.Add(row);
        }

        var root = new JsonObject { ["version"] = Version, ["mappings"] = mappings };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>The same mapping with one code's rows for a side replaced.</summary>
    public GlMappingResponse WithSide(string transactionCode, string side, params (string GlAccount, decimal Ratio, string Description)[] rows)
    {
        var kept = Mappings.Where(m => !(m.TransactionCode == transactionCode && m.Side == side)).ToList();
        var insertAt = Mappings.ToList().FindIndex(m => m.TransactionCode == transactionCode && m.Side == side);
        kept.InsertRange(Math.Max(0, Math.Min(insertAt, kept.Count)), rows.Select(r => new GlMappingRow(transactionCode, r.GlAccount, side, r.Ratio, r.Description)));
        return this with { Mappings = kept };
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"GL mapping {Version} ({Mappings.Count} rows)");
}
