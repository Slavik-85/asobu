using Asobu.Core.Hosting;

namespace Asobu.Core.Tests;

/// <summary>
/// Reading the LAN port out of the game's own output.
///
/// The lines here are copied from real logs rather than written to suit the pattern — the whole
/// point of this source is that the launcher started the game and can hear it, so what it hears
/// is the thing worth testing against.
/// </summary>
public class LanPortWatchTests
{
    [Theory]
    // 1.13 and later, taken from a 1.19.2 Forge session.
    [InlineData("[22:47:52] [Render thread/INFO] [minecraft/IntegratedServer]: Started serving on 60731", 60731)]
    [InlineData("[10:02:11] [Server thread/INFO]: Started serving on 25565", 25565)]
    // 1.8.9 and earlier name the address as well.
    [InlineData("[10:02:11] [Server thread/INFO]: Started on 0.0.0.0:58212", 58212)]
    [InlineData("[10:02:11] [Server thread/INFO]: Started on 192.168.0.6:49001", 49001)]
    public void The_published_port_is_read_from_the_line(string line, int expected)
    {
        var watch = new LanPortWatch();
        watch.Note(line);

        Assert.Equal(expected, watch.Port);
    }

    [Theory]
    [InlineData("[10:02:11] [Server thread/INFO]: Preparing start region for dimension minecraft:overworld")]
    [InlineData("[10:02:11] [main/INFO]: Started serving on nothing")]
    [InlineData("")]
    [InlineData("Started serving on 99999")]
    public void Everything_else_leaves_it_alone(string line)
    {
        var watch = new LanPortWatch();
        watch.Note(line);

        Assert.Null(watch.Port);
    }

    [Fact]
    public void Nothing_is_open_until_something_says_so() => Assert.Null(new LanPortWatch().Port);

    [Fact]
    public void The_game_going_away_takes_the_world_with_it()
    {
        var watch = new LanPortWatch();
        watch.Note("[22:47:52] [Render thread/INFO] [minecraft/IntegratedServer]: Started serving on 60731");
        watch.Forget();

        Assert.Null(watch.Port);
    }

    /// <summary>
    /// A world can be closed and opened again without leaving the game, and it comes back on a
    /// different port — the newer line is the one that counts.
    /// </summary>
    [Fact]
    public void Reopening_moves_the_port()
    {
        var watch = new LanPortWatch();
        watch.Note("[minecraft/IntegratedServer]: Started serving on 60731");
        watch.Note("[minecraft/IntegratedServer]: Started serving on 49001");

        Assert.Equal(49001, watch.Port);
    }
}
