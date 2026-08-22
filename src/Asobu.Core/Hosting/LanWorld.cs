using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Asobu.Core.Hosting;

/// <summary>A world somebody has opened to LAN, as the game itself announces it.</summary>
public sealed record LanWorld(int Port, string Name);

/// <summary>
/// What the world says about itself when asked. The name comes from the beacon; this is the part
/// that changes, plus the version, which decides who can join at all.
/// </summary>
public sealed record WorldStatus(int Players, int MaxPlayers, string? Version = null);

/// <summary>
/// Finds the world the player just opened to LAN, by listening for the game's own announcement.
///
/// The game beacons <c>[MOTD]…[/MOTD][AD]port[/AD]</c> to 224.0.2.60:4445 every 1.5 seconds for as
/// long as the world is open. That is what the multiplayer screen's "Scanning for games on your
/// local network" reads, and it is the right source for two reasons: it stops the moment the world
/// closes, and the markers are byte-identical from 1.8.9 to 1.21.8 — checked in both jars. The
/// obvious alternative, scraping the game's log, is not: 1.8.9 prints "Started on …" and 1.21.8
/// prints "Started serving on …", so a log parser needs a table of per-version spellings and
/// still learns nothing when the world closes.
///
/// The beacon reaches us on the same machine because multicast loops back by default, which is
/// the only case that matters — Asobu is watching the game it launched.
/// </summary>
public static class LanBeacon
{
    private static readonly IPAddress Group = IPAddress.Parse("224.0.2.60");
    private const int BeaconPort = 4445;

    /// <summary>
    /// Waits for the next announcement, or gives up. Callers poll this rather than subscribe: a
    /// world that has closed simply stops announcing, so "no beacon within a few seconds" is the
    /// signal that hosting is over, and a polling caller gets that for free.
    /// </summary>
    public static async Task<LanWorld?> FindAsync(TimeSpan within, CancellationToken cancellationToken = default)
    {
        using var socket = new UdpClient();

        // Bound shared, because the game — and any other launcher the player has open — is
        // listening on this same port. Claiming it exclusively would be taking it from them.
        socket.ExclusiveAddressUse = false;
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, BeaconPort));

        try
        {
            socket.JoinMulticastGroup(Group);
        }
        catch (SocketException)
        {
            // No interface that can carry multicast. Nothing to wait for, so say so now rather
            // than making the caller sit out the timeout.
            return null;
        }

        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        giveUp.CancelAfter(within);

        try
        {
            while (true)
            {
                var packet = await socket.ReceiveAsync(giveUp.Token).ConfigureAwait(false);
                if (Parse(Encoding.UTF8.GetString(packet.Buffer)) is { } world) return world;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pulls the port and the world's name out of one announcement. Anything malformed is simply
    /// not a beacon — the port is shared with whatever else uses multicast on this network.
    /// </summary>
    internal static LanWorld? Parse(string payload)
    {
        if (Between(payload, "[AD]", "[/AD]") is not { } advertised) return null;
        if (!int.TryParse(advertised.Trim(), out var port) || port is < 1 or > 65535) return null;

        var name = Between(payload, "[MOTD]", "[/MOTD]")?.Trim();
        return new LanWorld(port, string.IsNullOrEmpty(name) ? "A world" : name);
    }

    private static string? Between(string text, string open, string close)
    {
        var from = text.IndexOf(open, StringComparison.Ordinal);
        if (from < 0) return null;

        from += open.Length;

        var to = text.IndexOf(close, from, StringComparison.Ordinal);
        return to < 0 ? null : text[from..to];
    }
}

/// <summary>
/// Asks a world how many people are in it, using the same status ping the multiplayer screen uses.
///
/// Two packets out, one back. Worth the exchange rather than guessing from the beacon: the beacon
/// carries a name and a port and nothing else, and "2/8" is the part of a friend's world that
/// changes minute to minute.
/// </summary>
public static class ServerPing
{
    /// <summary>
    /// Reported to the server as the protocol we speak. -1 is the conventional "just asking"
    /// value; a status ping is answered whatever goes here, and claiming a real version number
    /// would invite a version-mismatch reply we would then have to ignore.
    /// </summary>
    private const int Unspecified = -1;

    private const int StatusIntent = 1;

    public static async Task<WorldStatus?> QueryAsync(
        string host, int port, TimeSpan within, CancellationToken cancellationToken = default)
    {
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        giveUp.CancelAfter(within);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, giveUp.Token).ConfigureAwait(false);

            await using var stream = client.GetStream();

            await SendAsync(stream, Handshake(host, port), giveUp.Token).ConfigureAwait(false);
            await SendAsync(stream, [0x00], giveUp.Token).ConfigureAwait(false);   // status request

            var packet = await McProtocol.ReadPacketAsync(stream, giveUp.Token).ConfigureAwait(false);

            var at = 0;
            if (McProtocol.ReadVarInt(packet.Body, ref at) != 0x00) return null;

            return ReadStatus(McProtocol.ReadString(packet.Body, ref at));
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or JsonException)
        {
            // A world that has just closed, or a port that was never one. Either way there is
            // nothing to report, and nothing here is worth failing a friends list over.
            return null;
        }
    }

    private static byte[] Handshake(string host, int port)
    {
        using var body = new MemoryStream();

        McProtocol.WriteVarInt(body, 0x00);
        McProtocol.WriteVarInt(body, Unspecified);
        McProtocol.WriteString(body, host);
        body.WriteByte((byte)(port >> 8));
        body.WriteByte((byte)port);
        McProtocol.WriteVarInt(body, StatusIntent);

        return body.ToArray();
    }

    private static async Task SendAsync(Stream to, byte[] body, CancellationToken cancellationToken)
    {
        using var framed = new MemoryStream();
        McProtocol.WriteVarInt(framed, body.Length);
        framed.Write(body);

        await to.WriteAsync(framed.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the counts and the version. The reply also carries a description, a favicon and a
    /// sample of player names, none of which is wanted here — the beacon already said what the
    /// world is called, and the rest would need chat-component parsing to use.
    /// </summary>
    private static WorldStatus? ReadStatus(string json)
    {
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("players", out var players)) return null;

        var online = players.TryGetProperty("online", out var o) && o.TryGetInt32(out var count) ? count : 0;
        var max = players.TryGetProperty("max", out var m) && m.TryGetInt32(out var limit) ? limit : 0;

        var version = document.RootElement.TryGetProperty("version", out var v)
            && v.TryGetProperty("name", out var named) ? named.GetString() : null;

        return new WorldStatus(online, max, version);
    }
}
