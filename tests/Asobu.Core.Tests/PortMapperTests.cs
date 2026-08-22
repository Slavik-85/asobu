using System.Net;
using Asobu.Core.Hosting;

namespace Asobu.Core.Tests;

/// <summary>
/// Asking the router to let people in.
///
/// The parts worth testing are the ones that decide whether an answer is any good, because a
/// router that maps a port and hands back an address nobody can reach reports success either way.
/// </summary>
public class PortMapperTests
{
    /// <summary>
    /// The trap this exists for: a router behind another router, or behind the carrier's, maps
    /// the port and answers with a private address. Publishing it spends a friend's probe on a
    /// route that was never going to work.
    /// </summary>
    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.4.9")]
    [InlineData("172.31.255.1")]
    [InlineData("192.168.0.1")]
    [InlineData("169.254.3.4")]
    [InlineData("127.0.0.1")]
    [InlineData("100.64.0.1")]     // carrier-grade NAT
    [InlineData("100.127.255.1")]
    public void An_address_nobody_could_dial_is_not_worth_publishing(string address) =>
        Assert.False(PortMapper.Reachable(IPAddress.Parse(address)));

    [Theory]
    [InlineData("81.2.69.142")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]     // just outside 172.16/12
    [InlineData("100.63.255.1")]   // just outside 100.64/10
    [InlineData("100.128.0.1")]
    [InlineData("192.169.0.1")]    // just outside 192.168/16
    public void A_real_address_is(string address) =>
        Assert.True(PortMapper.Reachable(IPAddress.Parse(address)));

    [Fact]
    public void Nothing_at_all_is_not_an_address() => Assert.False(PortMapper.Reachable(null));

    // ---- Reading what the router says ----

    [Fact]
    public void The_description_is_found_in_the_ssdp_reply()
    {
        var reply = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\n"
            + "LOCATION: http://192.168.0.1:5000/rootDesc.xml\r\nST: upnp:rootdevice\r\n\r\n";

        Assert.Equal("http://192.168.0.1:5000/rootDesc.xml", PortMapper.Location(reply)?.ToString());
    }

    /// <summary>Routers are inconsistent about header case, and the specification allows it.</summary>
    [Fact]
    public void However_the_router_spells_the_header()
    {
        var reply = "HTTP/1.1 200 OK\r\nlocation: http://10.0.0.1/igd.xml\r\n\r\n";

        Assert.Equal("http://10.0.0.1/igd.xml", PortMapper.Location(reply)?.ToString());
    }

    [Fact]
    public void A_reply_that_says_nothing_useful_gives_nothing() =>
        Assert.Null(PortMapper.Location("HTTP/1.1 200 OK\r\nServer: something\r\n\r\n"));

    /// <summary>
    /// The service is named WANIPConnection on a router with an ordinary connection and
    /// WANPPPConnection on one dialling PPPoE, and the control address is usually relative.
    /// </summary>
    [Theory]
    [InlineData("urn:schemas-upnp-org:service:WANIPConnection:1")]
    [InlineData("urn:schemas-upnp-org:service:WANPPPConnection:1")]
    [InlineData("urn:schemas-upnp-org:service:WANIPConnection:2")]
    public void The_port_forwarding_service_is_found_whichever_kind_it_is(string kind)
    {
        var description = $"""
            <?xml version="1.0"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0"><device><serviceList>
              <service>
                <serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType>
                <controlURL>/nope</controlURL>
              </service>
              <service>
                <serviceType>{kind}</serviceType>
                <controlURL>/ctl/IPConn</controlURL>
              </service>
            </serviceList></device></root>
            """;

        var control = PortMapper.ControlUrl(description, new Uri("http://192.168.0.1:5000/rootDesc.xml"));

        Assert.Equal("http://192.168.0.1:5000/ctl/IPConn", control?.ToString());
    }

    [Fact]
    public void A_router_offering_no_port_forwarding_gives_nothing()
    {
        var description = """
            <?xml version="1.0"?>
            <root><device><serviceList>
              <service>
                <serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType>
                <controlURL>/nope</controlURL>
              </service>
            </serviceList></device></root>
            """;

        Assert.Null(PortMapper.ControlUrl(description, new Uri("http://192.168.0.1/rootDesc.xml")));
    }

    /// <summary>A router answering with something that is not a document at all.</summary>
    [Fact]
    public void Nonsense_is_not_worth_throwing_over() =>
        Assert.Null(PortMapper.ControlUrl("<not xml", new Uri("http://192.168.0.1/x.xml")));
}
