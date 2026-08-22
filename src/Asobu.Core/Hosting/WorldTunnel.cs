using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Asobu.Core.Accounts;

namespace Asobu.Core.Hosting;

/// <summary>One person the host let in, and until when.</summary>
public sealed record Invite(string Uuid, string Username, DateTimeOffset Expires);

/// <summary>
/// The pass a guest shows at the door.
///
/// Signed by the host and checked by the host, so the network never holds a key that could mint
/// one. All the server ever does is carry the token from the person who made it to the person it
/// was made for; a server that lost every token it had handled could not forge a new one.
///
/// Minecraft has nothing to offer here — a world opened to LAN has no whitelist and no ops beyond
/// its owner, and takes anyone who can reach the port. So the guest list has to live in front of
/// the game rather than inside it, which is what this is.
/// </summary>
public static class InviteToken
{
    /// <summary>Names this signature, so a token cannot be replayed as some other kind of proof.</summary>
    private const string Version = "asobu.invite.v1";

    /// <summary>Splits the fields. Minecraft usernames and UUIDs contain neither this nor a newline.</summary>
    private const char Separator = '|';

    public static string Mint(byte[] secret, string uuid, string username, DateTimeOffset expires)
    {
        if (uuid.Contains(Separator) || username.Contains(Separator))
            throw new ArgumentException("A name or id with a separator in it is not one Minecraft would issue.");

        var payload = Encoding.UTF8.GetBytes(string.Join(Separator,
            uuid, username, expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));

        return Base64Url.EncodeToString(payload) + "." + Base64Url.EncodeToString(Sign(secret, payload));
    }

    /// <summary>
    /// Null for anything that isn't a live pass this host signed — a forgery, a token from before
    /// the secret was rotated, one that expired, or gibberish. The caller gets no reason, because
    /// the caller is about to talk to whoever sent it and telling them which part failed is how
    /// they find the part that doesn't.
    /// </summary>
    public static Invite? Verify(byte[] secret, string token, DateTimeOffset now)
    {
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1) return null;

        byte[] payload, mac;
        try
        {
            payload = Base64Url.DecodeFromChars(token.AsSpan(0, dot));
            mac = Base64Url.DecodeFromChars(token.AsSpan(dot + 1));
        }
        catch (FormatException)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(mac, Sign(secret, payload))) return null;

        var fields = Encoding.UTF8.GetString(payload).Split(Separator);
        if (fields.Length != 3) return null;
        if (!long.TryParse(fields[2], CultureInfo.InvariantCulture, out var unix)) return null;

        var expires = DateTimeOffset.FromUnixTimeSeconds(unix);
        return expires <= now ? null : new Invite(fields[0], fields[1], expires);
    }

    private static byte[] Sign(byte[] secret, byte[] payload)
    {
        byte[] signed = [.. Encoding.UTF8.GetBytes(Version), .. payload];
        return HMACSHA256.HashData(secret, signed);
    }
}

/// <summary>
/// The key the host signs invites with, kept beside the Microsoft refresh token — same vault,
/// same protection at rest. Per account rather than per machine, so signing in elsewhere and
/// re-inviting the same friends works, and losing the machine invalidates every outstanding pass.
/// </summary>
public static class HostSecret
{
    public static byte[] For(TokenVault vault, string accountUuid)
    {
        var key = "hostkey:" + accountUuid;

        if (vault.Get(key) is { Length: > 0 } stored)
        {
            try { return Convert.FromBase64String(stored); }
            catch (FormatException) { /* Unreadable is the same as absent: mint a new one. */ }
        }

        var secret = RandomNumberGenerator.GetBytes(32);
        vault.Set(key, Convert.ToBase64String(secret));
        return secret;
    }
}

/// <summary>
/// Stands in front of a world opened to LAN and lets in the people who were invited.
///
/// Nothing here understands the game. It reads the pass, reads far enough into the connection to
/// learn which player is arriving, and from then on moves bytes — so compression and encryption
/// negotiated further down the stream go through untouched, and a new Minecraft version needs no
/// change to any of this.
///
/// The username check is not politeness. A LAN world admits anyone connecting under the host's own
/// username <i>as the host</i>, owner privileges included — verified in the 1.21.8 bytecode, where
/// the login handler compares the incoming name against the singleplayer profile before it does
/// anything else. Since an offline Asobu account may pick any name it likes, that door has to be
/// held shut here.
/// </summary>
public sealed class WorldDoorman : IDisposable
{
    private readonly byte[] _secret;
    private readonly string _hostUsername;
    private readonly int _lanPort;
    private readonly TcpListener _listener;

    /// <summary>
    /// Long enough for a slow link, short enough that a connection which says nothing is not a
    /// way to leave sockets lying around.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public WorldDoorman(byte[] secret, string hostUsername, int lanPort)
    {
        _secret = secret;
        _hostUsername = hostUsername;
        _lanPort = lanPort;

        // Any address, because the whole point is to be reachable from another machine, and port
        // zero because the port is published through the friends network rather than agreed in
        // advance — a fixed one would only be a thing to collide with.
        _listener = new TcpListener(IPAddress.Any, 0);

        // Willing to share the port before it is taken, because punching needs a second socket
        // dialling out from this same one to open the way back in. Set before Start, since after
        // it the port is already claimed.
        Reflection.Share(_listener.Server);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    /// <summary>Raised when somebody is let in or turned away, for the host to see who knocked.</summary>
    public event Action<Invite?, string>? Knocked;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var guest = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = ServeAsync(guest, cancellationToken);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The world closed, or the host stopped hosting. Both end the loop the same way.
        }
    }

    private async Task ServeAsync(TcpClient guest, CancellationToken cancellationToken)
    {
        using var _ = guest;

        try
        {
            using var greeting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            greeting.CancelAfter(Patience);

            await using var fromGuest = guest.GetStream();

            var invite = await AdmitAsync(fromGuest, greeting.Token).ConfigureAwait(false);
            if (invite is null) return;

            // Everything the guest has already said that belongs to the game, kept so the world
            // receives the connection exactly as the client opened it.
            var opening = await ReadOpeningAsync(fromGuest, invite, greeting.Token).ConfigureAwait(false);
            if (opening is null) return;

            using var world = new TcpClient();
            await world.ConnectAsync(IPAddress.Loopback, _lanPort, cancellationToken).ConfigureAwait(false);

            await using var toWorld = world.GetStream();
            await toWorld.WriteAsync(opening, cancellationToken).ConfigureAwait(false);

            await RelayAsync(fromGuest, toWorld, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // A guest who left, or a world that closed under them.
        }
    }

    /// <summary>Reads the pass and answers. Null means the connection is over.</summary>
    private async Task<Invite?> AdmitAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = await Handshake.ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);

        var refusal = Check(line, out var invite);
        if (refusal is null)
        {
            await Handshake.WriteLineAsync(stream, Handshake.Accepted, cancellationToken).ConfigureAwait(false);
            Knocked?.Invoke(invite, "in");
            return invite;
        }

        // Refusals all say the same word. Which check failed is the host's business, not the
        // caller's — a door that explains itself is a door somebody can work backwards through.
        await Handshake.WriteLineAsync(stream, Handshake.Refused, cancellationToken).ConfigureAwait(false);
        Knocked?.Invoke(invite, refusal);
        return null;
    }

    private string? Check(string? line, out Invite? invite)
    {
        invite = null;

        if (line is null || !line.StartsWith(Handshake.Greeting + " ", StringComparison.Ordinal))
            return "not asobu";

        invite = InviteToken.Verify(_secret, line[(Handshake.Greeting.Length + 1)..], DateTimeOffset.UtcNow);
        if (invite is null) return "no invite";

        return invite.Username.Equals(_hostUsername, StringComparison.OrdinalIgnoreCase)
            ? "claimed the host's name"
            : null;
    }

    /// <summary>
    /// Reads the handshake, and the login packet behind it when the guest is actually joining,
    /// checking the name against the pass. Returns the bytes to replay to the world.
    /// </summary>
    private static async Task<byte[]?> ReadOpeningAsync(Stream stream, Invite invite, CancellationToken cancellationToken)
    {
        var handshake = await McProtocol.ReadPacketAsync(stream, cancellationToken).ConfigureAwait(false);

        var at = 0;
        if (McProtocol.ReadVarInt(handshake.Body, ref at) != 0x00) return null;

        McProtocol.ReadVarInt(handshake.Body, ref at);       // protocol version, not ours to care about
        McProtocol.ReadString(handshake.Body, ref at);       // the address the client dialled
        McProtocol.ReadUShort(handshake.Body, ref at);
        var intent = McProtocol.ReadVarInt(handshake.Body, ref at);

        // A status ping brings nobody into the world, so there is no name to check and no reason
        // to make the friends list wait for a login that isn't coming.
        if (intent != Handshake.LoginIntent) return handshake.Raw;

        var login = await McProtocol.ReadPacketAsync(stream, cancellationToken).ConfigureAwait(false);

        at = 0;
        if (McProtocol.ReadVarInt(login.Body, ref at) != 0x00) return null;

        var username = McProtocol.ReadString(login.Body, ref at);
        if (!username.Equals(invite.Username, StringComparison.OrdinalIgnoreCase)) return null;

        return [.. handshake.Raw, .. login.Raw];
    }

    internal static async Task RelayAsync(Stream one, Stream other, CancellationToken cancellationToken)
    {
        var forward = one.CopyToAsync(other, cancellationToken);
        var back = other.CopyToAsync(one, cancellationToken);

        // Whichever direction ends first ends the conversation: the other side is talking to
        // somebody who has hung up.
        await Task.WhenAny(forward, back).ConfigureAwait(false);
    }

    public void Dispose() => _listener.Dispose();
}

/// <summary>
/// Knocking on somebody so that their answer is let back in.
///
/// A router drops an incoming connection nobody asked for. It stops dropping them once something
/// inside has sent to that address, because then the reply looks like one. So both machines fire
/// at each other at the same moment and each router believes its own user started it. These
/// attempts are expected to fail; opening the way is the whole of their job.
/// </summary>
public static class Punch
{
    /// <summary>A few, because the first can arrive before the other side has fired at all.</summary>
    private const int Attempts = 5;

    private static readonly TimeSpan Between = TimeSpan.FromMilliseconds(250);

    public static async Task AtAsync(int fromPort, string address, CancellationToken cancellationToken)
    {
        if (!IPEndPoint.TryParse(address, out var peer)) return;

        for (var attempt = 0; attempt < Attempts && !cancellationToken.IsCancellationRequested; attempt++)
        {
            using var socket = Reflection.Bind(fromPort);

            try
            {
                // Short, because this is not trying to succeed. A connection that does is closed
                // straight away; the guest's own attempt is the one that carries the session.
                using var brief = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                brief.CancelAfter(Between);

                await socket.ConnectAsync(peer, brief.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Refused, ignored, or timed out. The packet still left, which is what matters.
            }

            try
            {
                await Task.Delay(Between, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

/// <summary>
/// Every address this machine's door can be reached at, as this machine sees them.
///
/// Offered to friends ahead of the address the server saw us arrive from, because a friend on the
/// same network — or the same VPN, which is how most people work around this problem today —
/// connects without the traffic ever leaving it. The server adds the public one after these.
/// </summary>
public static class LocalAddresses
{
    public static IReadOnlyList<string> For(int port) =>
    [
        .. NetworkInterface.GetAllNetworkInterfaces()
            .Where(card => card.OperationalStatus == OperationalStatus.Up
                        && card.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(card => card.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .Where(Worth)
            .Select(address => address.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{address}]:{port}"   // the brackets are what tells a port from the address
                : $"{address}:{port}")
            .Distinct()
    ];

    /// <summary>
    /// Whether an address is one somebody else could plausibly use.
    ///
    /// IPv6 is worth offering because there is no NAT in front of it: where both ends have a real
    /// one and the firewall allows it, the connection simply happens and nothing is relayed. But
    /// only a real one. Link-local is meaningless off the wire it is on, unique-local is the IPv6
    /// spelling of 192.168, and Teredo and 6to4 are tunnels that are usually either broken or so
    /// slow that offering them only spends a probe finding that out.
    /// </summary>
    private static bool Worth(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork) return true;
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;

        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Teredo) return false;

        var bytes = address.GetAddressBytes();

        // Unique local, fc00::/7: routable inside one network and nowhere else.
        if ((bytes[0] & 0xFE) == 0xFC) return false;

        // 6to4, 2002::/16.
        if (bytes[0] == 0x20 && bytes[1] == 0x02) return false;

        // Global unicast is 2000::/3. Everything outside it is multicast or otherwise not an
        // address anybody dials.
        return (bytes[0] & 0xE0) == 0x20;
    }
}

/// <summary>
/// The guest's end: a door on this machine that the game can walk through.
///
/// Bound to the loopback address on purpose. Nothing outside this machine may use it, and — the
/// practical half — Windows Firewall does not ask about a socket that only listens to 127.0.0.1,
/// so joining a friend's world costs the guest no dialog at all.
/// </summary>
public sealed class WorldGuest : IDisposable
{
    private readonly Func<CancellationToken, Task<Stream>> _reach;
    private readonly string _token;
    private readonly TcpListener _listener;

    /// <summary>
    /// <paramref name="reach"/> is however the host is got hold of: a socket to their machine when
    /// there is a route to it, or a relayed connection when there is not. Everything past that
    /// point is the same either way, which is the point of taking it as a stream.
    /// </summary>
    public WorldGuest(Func<CancellationToken, Task<Stream>> reach, string token)
    {
        _reach = reach;
        _token = token;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>Hand this to the game as 127.0.0.1:&lt;port&gt; and it joins the friend's world.</summary>
    public int Port { get; }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var game = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = ForwardAsync(game, cancellationToken);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task ForwardAsync(TcpClient game, CancellationToken cancellationToken)
    {
        using var _ = game;

        try
        {
            await using var toHost = await _reach(cancellationToken).ConfigureAwait(false);

            await Handshake.WriteLineAsync(toHost, $"{Handshake.Greeting} {_token}", cancellationToken).ConfigureAwait(false);

            var answer = await Handshake.ReadLineAsync(toHost, cancellationToken).ConfigureAwait(false);
            if (answer != Handshake.Accepted) return;

            await using var fromGame = game.GetStream();
            await WorldDoorman.RelayAsync(fromGame, toHost, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public void Dispose() => _listener.Dispose();
}

/// <summary>
/// The few lines the two ends of the tunnel say to each other before the game takes over. A line
/// protocol rather than a framed one because it is read once per connection, by hand, one byte at
/// a time — reading ahead would swallow the first bytes of the game's own conversation.
/// </summary>
internal static class Handshake
{
    internal const string Greeting = "ASOBU1";
    internal const string Accepted = "OK";
    internal const string Refused = "NO";

    /// <summary>Handshake in the Minecraft sense: the client means to log in, not just look.</summary>
    internal const int LoginIntent = 2;

    /// <summary>A greeting longer than this is not one. Tokens run to roughly a hundred bytes.</summary>
    private const int LongestLine = 512;

    internal static async Task<string?> ReadLineAsync(Stream from, CancellationToken cancellationToken)
    {
        var line = new List<byte>(96);
        var one = new byte[1];

        while (line.Count < LongestLine)
        {
            if (await from.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0) return null;
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString([.. line]);

            line.Add(one[0]);
        }

        return null;
    }

    internal static Task WriteLineAsync(Stream to, string line, CancellationToken cancellationToken) =>
        to.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancellationToken).AsTask();
}
