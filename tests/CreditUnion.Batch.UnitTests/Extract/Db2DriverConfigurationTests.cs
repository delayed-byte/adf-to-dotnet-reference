using CreditUnion.Batch.KeyStone;

namespace CreditUnion.Batch.UnitTests.Extract;

public sealed class Db2DriverConfigurationTests
{
    [Fact]
    public void The_generated_driver_configuration_disables_client_reroute_for_each_endpoint()
    {
        var xml = Db2DriverConfiguration.BuildConfig([new Db2Endpoint("KEYSTONE", "db2.example", 50000), new Db2Endpoint("KEYSTONE", "127.0.0.1", 15600)]);

        xml.ShouldContain("<database name=\"KEYSTONE\" host=\"db2.example\" port=\"50000\">");
        xml.ShouldContain("<database name=\"KEYSTONE\" host=\"127.0.0.1\" port=\"15600\">");
        xml.Split("enableACR\" value=\"false\"").Length.ShouldBe(3, "once per endpoint");
    }

    [Fact]
    public void Names_are_xml_escaped()
    {
        Db2DriverConfiguration.BuildConfig([new Db2Endpoint("K\"<>", "h&st", 1)]).ShouldContain("name=\"K&quot;&lt;&gt;\" host=\"h&amp;st\"");
    }

    [Fact]
    public void The_endpoint_is_read_from_the_connection_string_with_Db2s_default_port()
    {
        Db2DriverConfiguration.EndpointOf("Server=db2.example:50123;Database=KEYSTONE;UID=u;PWD=p;").ShouldBe(new Db2Endpoint("KEYSTONE", "db2.example", 50123));
        Db2DriverConfiguration.EndpointOf("Server=db2.example;Database=KEYSTONE;UID=u;PWD=p;").Port.ShouldBe(50000);
    }

    [Fact]
    public void An_empty_connection_string_configures_nothing()
    {
        Db2DriverConfiguration.DisableClientReroute((string?)null).ShouldBeNull();
        Db2DriverConfiguration.DisableClientReroute(" ").ShouldBeNull();
    }
}
