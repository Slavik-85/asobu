using System.Security.Cryptography;
using Asobu.Core.Hosting;

namespace Asobu.Core.Tests;

/// <summary>
/// The loop that notices a world being opened to LAN and puts a door in front of it.
///
/// Driven a turn at a time with the beacon and the status ping standing in for the network, so
/// what is being tested is the decision-making — when to raise the door, when to take it down,
/// and when to bother the friends network — rather than anyone's sockets.
/// </summary>
public class WorldHostTests
{
    private static readonly byte[] Secret = RandomNumberGenerator.GetBytes(32);

    /// <summary>A host wired to a beacon and a status ping the test controls.</summary>
    private sealed class Fake : IDisposable
    {
        public LanWorld? Beacon;
        public WorldStatus? Status = new(2, 8);
        public readonly List<HostedWorld?> Published = [];

        public readonly WorldHost Host;

        public Fake()
        {
            Host = new WorldHost(Secret, "Slavik",
                _ => Task.FromResult<LanWorld?>(Beacon),
                (_, _) => Task.FromResult<WorldStatus?>(Status));

            Host.Changed += world => Published.Add(world);
        }

        public Task TurnAsync() => Host.OnceAsync(CancellationToken.None);

        public void Dispose() => Host.Dispose();
    }

    [Fact]
    public async Task Opening_a_world_raises_a_door_and_describes_it()
    {
        using var fake = new Fake { Beacon = new LanWorld(58212, "Slavik - Skyblock") };

        await fake.TurnAsync();

        Assert.NotNull(fake.Host.Current);
        Assert.Equal("Slavik - Skyblock", fake.Host.Current.Name);
        Assert.Equal(2, fake.Host.Current.Players);
        Assert.Equal(8, fake.Host.Current.MaxPlayers);

        // And there is a real door listening, on a port that is not the world's own.
        Assert.NotEqual(0, fake.Host.Current.DoormanPort);
        Assert.NotEqual(58212, fake.Host.Current.DoormanPort);
    }

    [Fact]
    public async Task A_world_that_says_nothing_about_itself_is_still_hosted()
    {
        using var fake = new Fake { Beacon = new LanWorld(58212, "A world"), Status = null };

        await fake.TurnAsync();

        Assert.NotNull(fake.Host.Current);
        Assert.Equal(0, fake.Host.Current.Players);
    }

    /// <summary>
    /// One empty window is a dropped packet, not a closed world. Acting on it would take the door
    /// down and drop everybody behind it.
    /// </summary>
    [Fact]
    public async Task One_missed_beacon_does_not_close_the_world()
    {
        using var fake = new Fake { Beacon = new LanWorld(58212, "Slavik - Skyblock") };
        await fake.TurnAsync();

        fake.Beacon = null;
        await fake.TurnAsync();

        Assert.NotNull(fake.Host.Current);
    }

    [Fact]
    public async Task Two_missed_beacons_close_the_world()
    {
        using var fake = new Fake { Beacon = new LanWorld(58212, "Slavik - Skyblock") };
        await fake.TurnAsync();

        fake.Beacon = null;
        await fake.TurnAsync();
        await fake.TurnAsync();

        Assert.Null(fake.Host.Current);
        Assert.Equal([null], fake.Published[1..]);
    }

    [Fact]
    public async Task A_beacon_returning_after_a_hiccup_forgets_it()
    {
        using var fake = new Fake { Beacon = new LanWorld(58212, "Slavik - Skyblock") };
        await fake.TurnAsync();

        fake.Beacon = null;
        await fake.TurnAsync();

        fake.Beacon = new LanWorld(58212, "Slavik - Skyblock");
        await fake.TurnAsync();

        fake.Beacon = null;
        await fake.TurnAsync();

        // The earlier miss was forgotten, so this one is the first of two rather than the second.
        Assert.NotNull(fake.Host.Current);
    }

    [Fact]
    public async Task Reopening_on_another_port_gets_another_door()
    {
        using var fake = new Fake { Beacon = new LanWorld(58212, "Slavik - Skyblock") };
        await fake.TurnAsync();
        var first = fake.Host.Current!.DoormanPort;

        fake.Beacon = new LanWorld(49001, "Slavik - Skyblock");
        await fake.TurnAsync();

        Assert.NotEqual(first, fake.Host.Current!.DoormanPort);
    }

    /// <summary>
    /// The loop runs every few seconds and the count usually hasn't moved. Saying so every time
    /// would have the friends network republishing the same sentence all evening.
    /// </summary>
    [Fact]
    public async Task Nothing_is_republished_while_nothing_changes()
    {
        using var fake = new Fake { Beacon = new LanWorld(58212, "Slavik - Skyblock") };

        await fake.TurnAsync();
        await fake.TurnAsync();
        await fake.TurnAsync();

        Assert.Single(fake.Published);
    }

    [Fact]
    public async Task Somebody_joining_is_worth_republishing()
    {
        using var fake = new Fake { Beacon = new LanWorld(58212, "Slavik - Skyblock") };
        await fake.TurnAsync();

        fake.Status = new WorldStatus(3, 8);
        await fake.TurnAsync();

        Assert.Equal(2, fake.Published.Count);
        Assert.Equal(3, fake.Published[^1]!.Players);
    }

    [Fact]
    public async Task Nothing_is_published_for_somebody_who_never_opened_a_world()
    {
        using var fake = new Fake { Beacon = null };

        await fake.TurnAsync();
        await fake.TurnAsync();
        await fake.TurnAsync();

        Assert.Null(fake.Host.Current);
        Assert.Empty(fake.Published);
    }
}
