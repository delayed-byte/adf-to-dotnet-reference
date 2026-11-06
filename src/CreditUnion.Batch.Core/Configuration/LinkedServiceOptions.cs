namespace CreditUnion.Batch.Core.Configuration;

// ADF linked services become options classes bound from configuration (PRD, ADF-to-.NET mapping).
// Secrets (passwords, connection strings, the webhook URL) come from user-secrets or environment
// variables locally and from Key Vault in Azure; none is committed.

/// <summary>LS_KeyStone_Db2: the read-only KSEXTRACT login on the KeyStone reporting copy.</summary>
public sealed class KeyStoneDb2Options
{
    public const string SectionName = "KeyStoneDb2";

    public string ConnectionString { get; set; } = string.Empty;
}

/// <summary>LS_Reporting_Sql: the reporting store, with the cu_batch_writer login.</summary>
public sealed class ReportingSqlOptions
{
    public const string SectionName = "ReportingSql";

    public string ConnectionString { get; set; } = string.Empty;
}

/// <summary>LS_Blob_Storage, plus the PL_IngestCardSettlement container parameters (their ADF defaults).</summary>
public sealed class BlobStorageOptions
{
    public const string SectionName = "BlobStorage";

    public string ConnectionString { get; set; } = string.Empty;

    public string LandingContainer { get; set; } = "card-landing";

    public string ArchiveContainer { get; set; } = "card-archive";

    public string QuarantineContainer { get; set; } = "card-quarantine";

    public string EvidenceContainer { get; set; } = "evidence";

    /// <summary>PL_IngestCardSettlement parameter retainUntil: the tag on archived and quarantined files.</summary>
    public string RetainUntil { get; set; } = "TBD";
}

/// <summary>The KeyBridge API (PL_MapAndAggregate parameter keyBridgeBaseUrl and Key Vault secret keybridge-api-password).</summary>
public sealed class KeyBridgeOptions
{
    public const string SectionName = "KeyBridge";

    public Uri? BaseUrl { get; set; }

    public string Username { get; set; } = "cu-batch";

    public string Password { get; set; } = string.Empty;
}

/// <summary>The reconciliation alert webhook (Key Vault secret recon-alert-webhook-url). The URL embeds a secret token.</summary>
public sealed class ReconAlertWebhookOptions
{
    public const string SectionName = "ReconAlertWebhook";

    public Uri? Url { get; set; }
}
