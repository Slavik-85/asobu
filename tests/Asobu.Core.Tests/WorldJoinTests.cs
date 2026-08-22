using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Asobu.Core.Hosting;

namespace Asobu.Core.Tests;

/// <summary>
/// Picking which of a host's addresses actually works from here.
///
/// The interesting cases are all failures — a LAN address that belongs to somebody else's LAN, a
/// pass that has run out — because those are what a player hits, and what they are told about it
/// is the whole of the feature from their side.
/// </summary>
public class WorldJoinTests : IDisposable
{
    private static readonly byte[] Secret = RandomNumberGenerator.GetBytes(32);

    private readonly CancellationTokenSource _stop = new();
    private readonly TcpListener _world = new(IPAddress.Loopback, 0);
    private readonly WorldDoorman _doorman;

    public WorldJoinTests()
    {
        _world.Start();

        // A world that greets whoever reaches it, so a successful join is visible as bytes.
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _world.AcceptTcpClientAsync(_stop.Token);
                _ = client.GetStream().WriteAsync("the world says hello"u8.ToArray(), _stop.Token);
            }
        });

        _doorman = new WorldDoorman(Secret, "Slavik", ((IPEndPoint)_world.LocalEndpoint).Port);
        _ = _doorman.RunAsync(_stop.Token);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _doorman.Dispose();
        _world.Dispose();
        _stop.Dispose();
    }

    private string Door => $"127.0.0.1:{_doorman.Port}";

    private static string Pass(string username = "Friend", int minutes = 5) =>
        InviteToken.Mint(Secret, "uuid-" + username, username, DateTimeOffset.UtcNow.AddMinutes(minutes));

    /// <summary>An address on a network this machine is not on. Nothing there will ever answer.</summary>
    private const string Elsewhere = "192.0.2.7:25565";

    [Fact]
    public async Task Reaches_the_host_and_opens_a_door_here()
    {
        using var join = await WorldJoin.ReachAsync([Door], Pass());

        Assert.Equal(Door, join.Door);
        Assert.StartsWith("127.0.0.1:", join.Address);

        // And the game can walk through it into the world.
        Assert.Equal("the world says hello", await SpeakThroughAsync(join));
    }

    /// <summary>
    /// The ordinary case for a friend on the internet: their LAN address is offered first and
    /// belongs to a network this machine has never seen.
    /// </summary>
    [Fact]
    public async Task An_address_on_somebody_elses_network_is_stepped_over()
    {
        using var join = await WorldJoin.ReachAsync([Elsewhere, Door], Pass());

        Assert.Equal(Door, join.Door);
        Assert.Equal("the world says hello", await SpeakThroughAsync(join));
    }

    [Fact]
    public async Task Being_turned_away_says_so_rather_than_blaming_the_network()
    {
        var refused = await Assert.ThrowsAsync<WorldJoinException>(
            () => WorldJoin.ReachAsync([Door], Pass(minutes: -1)));

        Assert.Contains("invited", refused.Message);
    }

    /// <summary>
    /// A door that said no and an address that said nothing are different problems, and the one
    /// worth reporting is the door's — the other addresses were never going to work anyway.
    /// </summary>
    [Fact]
    public async Task A_refusal_outranks_the_addresses_that_went_nowhere()
    {
        var refused = await Assert.ThrowsAsync<WorldJoinException>(
            () => WorldJoin.ReachAsync([Elsewhere, Door], Pass(minutes: -1)));

        Assert.Contains("invited", refused.Message);
    }

    [Fact]
    public async Task Nowhere_to_reach_says_that_instead()
    {
        var unreachable = await Assert.ThrowsAsync<WorldJoinException>(
            () => WorldJoin.ReachAsync([Elsewhere], Pass()));

        Assert.Contains("Couldn't reach", unreachable.Message);
    }

    [Fact]
    public async Task A_host_who_has_not_said_where_they_are_is_not_an_error_worth_blaming_them_for()
    {
        var nothing = await Assert.ThrowsAsync<WorldJoinException>(
            () => WorldJoin.ReachAsync([], Pass()));

        Assert.Contains("haven't said where", nothing.Message);
    }

    [Fact]
    public async Task Gibberish_addresses_are_not_addresses()
    {
        var nothing = await Assert.ThrowsAsync<WorldJoinException>(
            () => WorldJoin.ReachAsync(["not-an-address", "1.2.3.4"], Pass()));

        Assert.Contains("haven't said where", nothing.Message);
    }

    /// <summary>Connects a pretend game to the local end and reads what the world sent back.</summary>
    private static async Task<string> SpeakThroughAsync(WorldJoin join)
    {
        var name = "Friend"u8.ToArray();
        var address = "127.0.0.1"u8.ToArray();

        byte[] shake = [0x00, 47, (byte)address.Length, .. address, 0x63, 0xDD, 0x02];
        byte[] login = [0x00, (byte)name.Length, .. name];
        byte[] opening = [(byte)shake.Length, .. shake, (byte)login.Length, .. login];

        using var game = new TcpClient();
        await game.ConnectAsync(IPAddress.Loopback, int.Parse(join.Address.Split(':')[1]));

        var stream = game.GetStream();
        await stream.WriteAsync(opening);

        var buffer = new byte[64];
        var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return Encoding.UTF8.GetString(buffer, 0, read);
    }
}
