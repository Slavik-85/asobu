using Asobu.Core.Servers;

namespace Asobu.Core.Tests;

/// <summary>
/// Which instances a suggested server will take.
///
/// The comparison is the whole of it. Get it wrong one way and somebody is told their instance
/// cannot join a server it can; wrong the other way and they launch, wait through a download,
/// and are turned away by the server instead.
/// </summary>
public class SuggestedServerTests
{
    [Theory]
    [InlineData("1.8", "1.8", 0)]
    [InlineData("1.8.9", "1.8", 1)]
    [InlineData("1.8", "1.8.9", -1)]
    [InlineData("1.21.11", "1.21.2", 1)]     // eleven is after two, not before it
    [InlineData("1.21.2", "1.21.11", -1)]
    [InlineData("1.21", "1.21.0", 0)]        // a missing part is a zero
    [InlineData("1.9", "1.10", -1)]          // and ten is after nine
    [InlineData("26.2", "1.21", 1)]
    public void Orders_versions_the_way_minecraft_does(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(GameVersions.Compare(left, right)));
    }

    /// <summary>
    /// A snapshot is named nothing like a release and does not sit anywhere on this scale.
    /// Rather than guess, it compares as equal — so a range never turns somebody away over a
    /// version the comparison could not read.
    /// </summary>
    [Theory]
    [InlineData("24w14a")]
    [InlineData("rd-132211")]
    [InlineData("infdev")]
    public void Refuses_to_rank_what_it_cannot_read(string odd)
    {
        Assert.Equal(0, GameVersions.Compare(odd, "1.21"));
    }

    /// <summary>
    /// A dotted pre-release is readable, and ranks where its number says. Only the undotted
    /// names — snapshots — are the ones with nowhere to sit.
    /// </summary>
    [Fact]
    public void Ranks_a_dotted_prerelease_by_its_number()
    {
        Assert.Equal(-1, Math.Sign(GameVersions.Compare("1.20-pre1", "1.21")));
    }

    /// <summary>A pre-release ranks beside the version it precedes rather than nowhere.</summary>
    [Fact]
    public void Reads_the_number_out_of_a_dotted_prerelease()
    {
        Assert.Equal(0, Math.Sign(GameVersions.Compare("1.8.9-pre2", "1.8.9")));
    }

    [Fact]
    public void A_floor_only_server_takes_everything_after_it()
    {
        var hypixel = SuggestedServers.All.Single(s => s.Name == "Hypixel");

        Assert.False(hypixel.Accepts("1.7.10"));
        Assert.True(hypixel.Accepts("1.8"));
        Assert.True(hypixel.Accepts("1.8.9"));
        Assert.True(hypixel.Accepts("1.21.4"));
    }

    [Fact]
    public void A_range_turns_away_both_ends()
    {
        var mineplex = SuggestedServers.All.Single(s => s.Name == "Mineplex");

        Assert.False(mineplex.Accepts("1.8.8"));
        Assert.True(mineplex.Accepts("1.8.9"));
        Assert.True(mineplex.Accepts("1.21"));
        Assert.False(mineplex.Accepts("1.21.1"));
    }

    /// <summary>
    /// MCC Island's floor is 1.21.11, which is the case that catches a comparison done on text:
    /// "1.21.11" sorts before "1.21.2" as a string and after it as a version.
    /// </summary>
    [Fact]
    public void A_two_digit_patch_is_newer_than_a_one_digit_one()
    {
        var mcc = SuggestedServers.All.Single(s => s.Name == "MCC Island");

        Assert.False(mcc.Accepts("1.21.2"));
        Assert.True(mcc.Accepts("1.21.11"));
        Assert.True(mcc.Accepts("1.21.12"));
    }

    [Fact]
    public void Every_suggestion_is_complete()
    {
        Assert.Equal(5, SuggestedServers.All.Count);

        foreach (var server in SuggestedServers.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(server.Name));
            Assert.False(string.IsNullOrWhiteSpace(server.VersionLabel));
            Assert.Contains('.', server.Address);
            Assert.DoesNotContain(' ', server.Address);
        }
    }

    /// <summary>
    /// Every range here is a claim about somebody else's server, and the only way to be right
    /// about one is to have looked. PvP Legacy went in as 1.8.x+ on the reasoning that a PvP
    /// server would want the old combat -- which was a guess, and wrong: it is 1.21.2+. Pinned so
    /// the corrected value is the one that has to be argued with next time.
    /// </summary>
    [Fact]
    public void Pvp_legacy_is_modern_only()
    {
        var legacy = SuggestedServers.All.Single(s => s.Name == "PvP Legacy");

        Assert.Equal("1.21.2+", legacy.VersionLabel);
        Assert.False(legacy.Accepts("1.8.9"));
        Assert.False(legacy.Accepts("1.21.1"));
        Assert.True(legacy.Accepts("1.21.2"));
        Assert.True(legacy.Accepts("1.21.4"));
    }

    /// <summary>What is shown and what is enforced have to be the same range.</summary>
    [Theory]
    [InlineData("Hypixel", "1.8")]
    [InlineData("Mineplex", "1.8.9")]
    [InlineData("MCC Island", "1.21.11")]
    [InlineData("PvP Club", "1.21.2")]
    [InlineData("PvP Legacy", "1.21.2")]
    public void The_label_agrees_with_the_floor(string name, string floor)
    {
        var server = SuggestedServers.All.Single(s => s.Name == name);

        Assert.StartsWith(floor, server.VersionLabel, StringComparison.Ordinal);
        Assert.True(server.Accepts(floor), $"{name} says {floor} and will not take it");
    }
}
