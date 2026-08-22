using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Asobu.Core.Hosting;

namespace Asobu.Core.Tests;

/// <summary>
/// The door in front of a world opened to LAN.
///
/// The game admits anybody who reaches the port, so everything keeping a world private is in this
/// tunnel — which makes the refusals the part worth testing hardest. The handshake bytes here are
/// written out by hand rather than through the launcher's own writer, so a mistake in that writer
/// shows up as a failure instead of cancelling itself out.
/// </summary>
public class WorldTunnelTests
{
    private static readonly byte[] Secret = RandomNumberGenerator.GetBytes(32);

    private static string PassFor(string username, int minutes = 5) =>
        InviteToken.Mint(Secret, "uuid-" + username, username, DateTimeOffset.UtcNow.AddMinutes(minutes));

    // ---- The pass ----

    [Fact]
    public void A_pass_the_host_signed_reads_back()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(30);
        var token = InviteToken.Mint(Secret, "abc-123", "Friend", expires);

        var invite = InviteToken.Verify(Secret, token, DateTimeOffset.UtcNow);

        Assert.NotNull(invite);
        Assert.Equal("abc-123", invite.Uuid);
        Assert.Equal("Friend", invite.Username);
        Assert.Equal(expires.ToUnixTimeSeconds(), invite.Expires.ToUnixTimeSeconds());
    }

    [Fact]
    public void A_pass_signed_by_somebody_else_is_not_a_pass()
    {
        var token = InviteToken.Mint(RandomNumberGenerator.GetBytes(32), "abc", "Friend", DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.Null(InviteToken.Verify(Secret, token, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Renaming_yourself_in_a_pass_invalidates_it()
    {
        var token = InviteToken.Mint(Secret, "abc", "Friend", DateTimeOffset.UtcNow.AddMinutes(5));

        // Swap the payload for one naming somebody else, keeping the signature.
        var forged = Convert.ToBase64String(Encoding.UTF8.GetBytes("abc|Slavik|99999999999"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + token[token.IndexOf('.')..];

        Assert.Null(InviteToken.Verify(Secret, forged, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_pass_stops_working_when_it_runs_out()
    {
        var token = InviteToken.Mint(Secret, "abc", "Friend", DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.Null(InviteToken.Verify(Secret, token, DateTimeOffset.UtcNow.AddMinutes(6)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("no-dot-here")]
    [InlineData(".")]
    [InlineData("!!!.!!!")]
    public void Gibberish_is_refused_rather_than_thrown_at(string token) =>
        Assert.Null(InviteToken.Verify(Secret, token, DateTimeOffset.UtcNow));

    // ---- The door ----

    [Fact]
    public async Task An_invited_friend_reaches_the_world()
    {
        using var world = new FakeWorld();
        using var gate = new Gate(world.Port);

        using var game = await gate.DialAsync(PassFor("Friend"));
        await game.SendAsync(Handshake("Friend"));

        Assert.Equal("the world says hello", await game.ReadTextAsync());

        // And the world was handed the connection exactly as the client opened it.
        Assert.Equal(Handshake("Friend"), await world.OpeningAsync(Handshake("Friend").Length));
    }

    [Fact]
    public async Task Somebody_without_a_pass_never_reaches_the_world()
    {
        using var world = new FakeWorld();
        using var gate = new Gate(world.Port);

        using var game = await gate.DialAsync("not-a-real-token");
        await game.SendAsync(Handshake("Stranger"));

        Assert.Equal(string.Empty, await game.ReadTextAsync());
        Assert.False(world.WasReached);
    }

    /// <summary>
    /// The one that matters most: a LAN world hands owner privileges to anyone arriving under the
    /// host's own username, and an offline account may pick any username it likes.
    /// </summary>
    [Fact]
    public async Task A_pass_in_the_hosts_own_name_is_refused_at_the_door()
    {
        using var world = new FakeWorld();
        using var gate = new Gate(world.Port);

        using var game = await gate.DialAsync(PassFor("Slavik"));
        await game.SendAsync(Handshake("Slavik"));

        Assert.Equal(string.Empty, await game.ReadTextAsync());
        Assert.False(world.WasReached);
    }

    [Fact]
    public async Task A_pass_cannot_be_lent_to_a_different_player()
    {
        using var world = new FakeWorld();
        using var gate = new Gate(world.Port);

        using var game = await gate.DialAsync(PassFor("Friend"));
        await game.SendAsync(Handshake("SomebodyElse"));

        Assert.Equal(string.Empty, await game.ReadTextAsync());
        Assert.False(world.WasReached);
    }

    [Fact]
    public async Task An_expired_pass_is_refused()
    {
        using var world = new FakeWorld();
        using var gate = new Gate(world.Port);

        using var game = await gate.DialAsync(PassFor("Friend", minutes: -1));
        await game.SendAsync(Handshake("Friend"));

        Assert.Equal(string.Empty, await game.ReadTextAsync());
        Assert.False(world.WasReached);
    }

    // ---- The beacon ----

    [Fact]
    public void The_beacon_gives_up_the_port_and_the_name()
    {
        var world = LanBeacon.Parse("[MOTD]Slavik - Skyblock[/MOTD][AD]58212[/AD]");

        Assert.NotNull(world);
        Assert.Equal(58212, world.Port);
        Assert.Equal("Slavik - Skyblock", world.Name);
    }

    [Fact]
    public void A_beacon_with_no_name_still_gives_up_the_port() =>
        Assert.Equal(25565, LanBeacon.Parse("[MOTD][/MOTD][AD]25565[/AD]")?.Port);

    [Theory]
    [InlineData("")]
    [InlineData("something else entirely")]
    [InlineData("[MOTD]A world[/MOTD]")]
    [InlineData("[MOTD]A world[/MOTD][AD]not a number[/AD]")]
    [InlineData("[MOTD]A world[/MOTD][AD]70000[/AD]")]
    [InlineData("[AD]58212")]
    public void Anything_that_is_not_a_beacon_is_ignored(string payload) =>
        Assert.Null(LanBeacon.Parse(payload));

    // ---- Scaffolding ----

    /// <summary>
    /// A client's opening bytes, hand-assembled: handshake (protocol 47, "127.0.0.1", 25565,
    /// intent 2) followed by login start. Every VarInt here is small enough to be one byte.
    /// </summary>
    private static byte[] Handshake(string username)
    {
        var address = "127.0.0.1"u8.ToArray();
        var name = Encoding.UTF8.GetBytes(username);

        byte[] shake = [0x00, 47, (byte)address.Length, .. address, 0x63, 0xDD, 0x02];
        byte[] login = [0x00, (byte)name.Length, .. name];

        return [(byte)shake.Length, .. shake, (byte)login.Length, .. login];
    }

    /// <summary>Stands in for the world opened to LAN: takes one connection and answers.</summary>
    private sealed class FakeWorld : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource<TcpClient> _arrived = new();

        public FakeWorld()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _ = Task.Run(async () =>
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _arrived.TrySetResult(client);
                    await client.GetStream().WriteAsync("the world says hello"u8.ToArray());
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException)
                {
                }
            });
        }

        public int Port { get; }

        public bool WasReached => _arrived.Task.IsCompleted;

        /// <summary>The bytes the doorman replayed on the world's behalf.</summary>
        public async Task<byte[]> OpeningAsync(int count)
        {
            var client = await _arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var buffer = new byte[count];
            await client.GetStream().ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            return buffer;
        }

        public void Dispose()
        {
            _listener.Dispose();
            if (_arrived.Task.IsCompletedSuccessfully) _arrived.Task.Result.Dispose();
        }
    }

    /// <summary>Both ends of the tunnel, wired to each other as they would be across the internet.</summary>
    private sealed class Gate : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly WorldDoorman _doorman;
        private WorldGuest? _guest;

        public Gate(int worldPort)
        {
            _doorman = new WorldDoorman(Secret, "Slavik", worldPort);
            _ = _doorman.RunAsync(_stop.Token);
        }

        /// <summary>How the guest's end gets hold of the door: straight to it, on this machine.</summary>
        private static Func<CancellationToken, Task<Stream>> Dial(int port) => async token =>
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, token);
            return client.GetStream();
        };

        /// <summary>Opens the guest's end with the given pass and connects a pretend game to it.</summary>
        public async Task<Wire> DialAsync(string token)
        {
            _guest = new WorldGuest(Dial(_doorman.Port), token);
            _ = _guest.RunAsync(_stop.Token);

            var game = new TcpClient();
            await game.ConnectAsync(IPAddress.Loopback, _guest.Port);
            return new Wire(game);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _guest?.Dispose();
            _doorman.Dispose();
            _stop.Dispose();
        }
    }

    /// <summary>A pretend game client on the guest's machine.</summary>
    private sealed class Wire(TcpClient client) : IDisposable
    {
        public Task SendAsync(byte[] bytes) => client.GetStream().WriteAsync(bytes).AsTask();

        /// <summary>
        /// What came back, or empty if the door shut instead. A refusal arrives either as a clean
        /// end of stream or as a reset — the guest's end drops a connection that still has the
        /// game's opening bytes sitting unread in it, and Windows answers unread data with an RST.
        /// Both mean the same thing here: nothing from the world.
        /// </summary>
        public async Task<string> ReadTextAsync()
        {
            var buffer = new byte[64];
            try
            {
                var read = await client.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                return Encoding.UTF8.GetString(buffer, 0, read);
            }
            catch (IOException e) when (e.InnerException is SocketException)
            {
                return string.Empty;
            }
        }

        public void Dispose() => client.Dispose();
    }
}
