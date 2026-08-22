using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace Asobu.Core.Hosting;

/// <summary>
/// Asks the router to let people in, so a friend can connect without anything being relayed.
///
/// Two protocols, because routers disagree about which they speak. NAT-PMP is a handful of bytes
/// to the gateway and answers in milliseconds when it is there at all. UPnP is a discovery packet,
/// an XML document and a SOAP call, and is the one most consumer routers offer. Both are tried,
/// NAT-PMP first because it costs almost nothing to find out.
///
/// Plenty of routers have both switched off, which is not a failure worth reporting: the world is
/// still hosted, guests still reach it through the relay, and nobody has to know the difference.
/// </summary>
public static class PortMapper
{
    /// <summary>How long the mapping lasts before the router forgets it, in seconds.</summary>
    private const int Lifetime = 3600;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Opens the way in, and returns the address to publish, or null when the router will not or
    /// the address it gives back is one nobody outside could use.
    /// </summary>
    public static async Task<string?> MapAsync(int port, CancellationToken cancellationToken = default)
    {
        foreach (var gateway in Gateways())
        {
            var mapped = await NatPmpAsync(gateway, port, cancellationToken).ConfigureAwait(false);
            if (mapped is not null) return mapped;
        }

        return await UpnpAsync(port, cancellationToken).ConfigureAwait(false);
    }

    private static List<IPAddress> Gateways() =>
    [
        .. NetworkInterface.GetAllNetworkInterfaces()
            .Where(card => card.OperationalStatus == OperationalStatus.Up)
            .SelectMany(card => card.GetIPProperties().GatewayAddresses)
            .Select(gateway => gateway.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork
                           && !address.Equals(IPAddress.Any))
            .Distinct()
    ];

    // ---- NAT-PMP ----

    private static async Task<string?> NatPmpAsync(IPAddress gateway, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var socket = new UdpClient();
            socket.Connect(gateway, 5351);

            using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            giveUp.CancelAfter(Patience);

            // Opcode 2 is "map a TCP port": version, opcode, two reserved bytes, the port here,
            // the port we would like out there, and how long to keep it.
            var ask = new byte[12];
            ask[1] = 2;
            BinaryPrimitives.WriteUInt16BigEndian(ask.AsSpan(4), (ushort)port);
            BinaryPrimitives.WriteUInt16BigEndian(ask.AsSpan(6), (ushort)port);
            BinaryPrimitives.WriteUInt32BigEndian(ask.AsSpan(8), Lifetime);

            await socket.SendAsync(ask, giveUp.Token).ConfigureAwait(false);
            var answer = (await socket.ReceiveAsync(giveUp.Token).ConfigureAwait(false)).Buffer;

            // Opcode 130 is the answer to 2, and a result of anything but zero is a refusal.
            if (answer.Length < 16 || answer[1] != 130) return null;
            if (BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(2)) != 0) return null;

            var outside = BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(10));

            // The mapping is made; where it can be reached from is a separate question.
            var address = await NatPmpAddressAsync(socket, giveUp.Token).ConfigureAwait(false);
            return Reachable(address) ? $"{address}:{outside}" : null;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static async Task<IPAddress?> NatPmpAddressAsync(UdpClient socket, CancellationToken cancellationToken)
    {
        await socket.SendAsync(new byte[2], cancellationToken).ConfigureAwait(false);
        var answer = (await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false)).Buffer;

        return answer.Length >= 12 && answer[1] == 128 ? new IPAddress(answer.AsSpan(8, 4)) : null;
    }

    // ---- UPnP ----

    private static async Task<string?> UpnpAsync(int port, CancellationToken cancellationToken)
    {
        var description = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (description is null) return null;

        using var http = new HttpClient { Timeout = Patience };

        string document;
        try
        {
            document = await http.GetStringAsync(description, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }

        if (ControlUrl(document, description) is not { } control) return null;

        var mine = LocalAddressToward(description);
        if (mine is null) return null;

        var mapped = await SoapAsync(http, control, "AddPortMapping", $"""
            <NewRemoteHost></NewRemoteHost>
            <NewExternalPort>{port}</NewExternalPort>
            <NewProtocol>TCP</NewProtocol>
            <NewInternalPort>{port}</NewInternalPort>
            <NewInternalClient>{mine}</NewInternalClient>
            <NewEnabled>1</NewEnabled>
            <NewPortMappingDescription>Asobu</NewPortMappingDescription>
            <NewLeaseDuration>{Lifetime}</NewLeaseDuration>
            """, cancellationToken).ConfigureAwait(false);

        if (mapped is null) return null;

        var outside = await SoapAsync(http, control, "GetExternalIPAddress", "", cancellationToken)
            .ConfigureAwait(false);

        if (outside is null) return null;

        var address = XDocument.Parse(outside).Descendants()
            .FirstOrDefault(node => node.Name.LocalName == "NewExternalIPAddress")?.Value;

        return IPAddress.TryParse(address, out var external) && Reachable(external)
            ? $"{external}:{port}"
            : null;
    }

    /// <summary>Finds a router that admits to being one. Null when none answers.</summary>
    private static async Task<Uri?> DiscoverAsync(CancellationToken cancellationToken)
    {
        const string search =
            "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n" +
            "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";

        try
        {
            using var socket = new UdpClient();
            using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            giveUp.CancelAfter(Patience);

            await socket.SendAsync(
                    Encoding.ASCII.GetBytes(search),
                    new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900),
                    giveUp.Token)
                .ConfigureAwait(false);

            while (true)
            {
                var reply = await socket.ReceiveAsync(giveUp.Token).ConfigureAwait(false);
                if (Location(Encoding.ASCII.GetString(reply.Buffer)) is { } where) return where;
            }
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>The LOCATION header out of an SSDP reply.</summary>
    internal static Uri? Location(string reply)
    {
        foreach (var line in reply.Split('\n'))
        {
            if (!line.StartsWith("location:", StringComparison.OrdinalIgnoreCase)) continue;

            return Uri.TryCreate(line[9..].Trim(), UriKind.Absolute, out var where) ? where : null;
        }

        return null;
    }

    /// <summary>
    /// Digs the address of the port-forwarding service out of the router's description. Routers
    /// offer either WANIPConnection or WANPPPConnection depending on how they reach the internet,
    /// and name their services with wildly varying capitalisation, so this matches loosely.
    /// </summary>
    internal static Uri? ControlUrl(string description, Uri from)
    {
        try
        {
            var services = XDocument.Parse(description).Descendants()
                .Where(node => node.Name.LocalName == "service");

            foreach (var service in services)
            {
                var kind = service.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value ?? "";
                if (!kind.Contains("WANIPConnection", StringComparison.OrdinalIgnoreCase)
                    && !kind.Contains("WANPPPConnection", StringComparison.OrdinalIgnoreCase)) continue;

                var control = service.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value;
                if (control is { Length: > 0 }) return new Uri(from, control);
            }
        }
        catch (System.Xml.XmlException)
        {
        }

        return null;
    }

    private static async Task<string?> SoapAsync(
        HttpClient http, Uri control, string action, string body, CancellationToken cancellationToken)
    {
        const string service = "urn:schemas-upnp-org:service:WANIPConnection:1";

        var envelope = $"""
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
            <s:Body><u:{action} xmlns:u="{service}">{body}</u:{action}></s:Body>
            </s:Envelope>
            """;

        using var request = new HttpRequestMessage(HttpMethod.Post, control)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml"),
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{service}#{action}\"");

        try
        {
            using var reply = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!reply.IsSuccessStatusCode) return null;

            return await reply.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>Which of this machine's addresses the router would see us at.</summary>
    private static IPAddress? LocalAddressToward(Uri router)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(router.Host, router.Port <= 0 ? 80 : router.Port);

            return (probe.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether an address anybody could actually dial. A router behind another router, or behind
    /// the carrier's own, hands back something private and reports success — a mapping that
    /// exists, on an address that goes nowhere. Publishing it would spend a friend's probe on a
    /// route that was never going to work.
    /// </summary>
    internal static bool Reachable(IPAddress? address)
    {
        if (address is null || IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;

        var bytes = address.GetAddressBytes();

        return bytes[0] switch
        {
            10 => false,                                        // 10/8
            127 or 0 => false,
            169 when bytes[1] == 254 => false,                  // link-local
            172 when bytes[1] >= 16 && bytes[1] <= 31 => false, // 172.16/12
            192 when bytes[1] == 168 => false,                  // 192.168/16
            100 when bytes[1] >= 64 && bytes[1] <= 127 => false,// carrier-grade NAT, 100.64/10
            _ => true,
        };
    }
}
