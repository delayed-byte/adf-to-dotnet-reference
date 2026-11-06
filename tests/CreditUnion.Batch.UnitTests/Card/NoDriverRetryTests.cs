using CreditUnion.Batch.Core.Configuration;
using CreditUnion.Batch.KeyStone;
using CreditUnion.Batch.KeyStone.Card;
using Microsoft.Data.SqlClient;

namespace CreditUnion.Batch.UnitTests.Card;

/// <summary>Nothing may retry underneath the orchestrator's RetryPolicy (ADR 0008, ADR 0010), or the attempts multiply and vanish from the audit trail.</summary>
public sealed class NoDriverRetryTests
{
    [Fact]
    public void The_Azure_Storage_SDK_retry_policy_is_switched_off()
    {
        var options = AzureCardBlobStore.CreateClientOptions();

        options.Retry.MaxRetries.ShouldBe(0);
    }

    [Fact]
    public void The_SqlClient_connection_retries_are_switched_off_even_when_the_connection_string_asks_for_them()
    {
        using var connection = Connections.CreateReportingConnection(
            new ReportingSqlOptions { ConnectionString = "Server=localhost;Database=x;User Id=u;Password=p;ConnectRetryCount=5;" });

        new SqlConnectionStringBuilder(connection.ConnectionString).ConnectRetryCount.ShouldBe(0);
    }
}
