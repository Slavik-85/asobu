using Asobu.Core.Mods;

namespace Asobu.Core.Tests;

/// <summary>
/// Finding the mod by halving, for the crashes where reading the report finds nothing.
///
/// The property that matters is that it always ends, always ends somewhere true, and never names
/// a mod it has not actually proved — so most of this drives the whole hunt to its end against a
/// known culprit and checks it arrives there, rather than checking one step at a time.
/// </summary>
public class ModBisectTests
{
    private static List<string> Pack(int count) =>
        [.. Enumerable.Range(1, count).Select(n => $"mod{n:000}.jar")];

    /// <summary>
    /// Plays out a whole bisect against a pack where one named mod is the culprit, answering each
    /// launch the way the game would: it crashes exactly when the culprit is switched on.
    /// </summary>
    private static (BisectVerdict Verdict, string? Found, int Launches) Hunt(
        IReadOnlyList<string> pack, string? culprit, int ceiling = 40)
    {
        var (verdict, state) = (BisectVerdict.Narrowing, ModBisect.Begin(pack));

        while (verdict == BisectVerdict.Narrowing && state.Launches < ceiling)
        {
            var crashed = culprit is not null && ModBisect.ShouldBeOn(state, culprit);

            (verdict, state) = ModBisect.Next(state, crashed);
        }

        return (verdict, ModBisect.Culprit(state), state.Launches);
    }

    // ---- it finds the one ----

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(64)]
    [InlineData(100)]
    [InlineData(457)]
    public void Whichever_mod_it_is_and_however_many_there_are(int count)
    {
        var pack = Pack(count);

        foreach (var culprit in pack)
        {
            var (verdict, found, _) = Hunt(pack, culprit);

            Assert.Equal(BisectVerdict.Found, verdict);
            Assert.Equal(culprit, found);
        }
    }

    /// <summary>
    /// The number that makes it worth doing. One at a time is a hundred launches; this is the
    /// first one to prove a mod is doing it at all, then one per halving.
    /// </summary>
    [Fact]
    public void A_hundred_mods_takes_about_eight_launches_rather_than_a_hundred()
    {
        var pack = Pack(100);
        var worst = pack.Max(culprit => Hunt(pack, culprit).Launches);

        Assert.InRange(worst, 1, 9);
    }

    // ---- and it knows when there isn't one ----

    /// <summary>
    /// A graphics driver, a full page file, the JIT. These crash just as reliably with every mod
    /// switched off, and a hunt that never asks would halve its way to some innocent mod and be
    /// believed. So the first launch of every bisect has everything off.
    /// </summary>
    [Fact]
    public void A_crash_that_happens_with_every_mod_off_is_not_a_mod()
    {
        var pack = Pack(50);

        var (verdict, state) = ModBisect.Next(ModBisect.Begin(pack), crashed: true);

        Assert.Equal(BisectVerdict.NotAMod, verdict);
        Assert.Equal(1, state.Launches);
    }

    [Fact]
    public void The_first_launch_has_everything_switched_off()
    {
        var pack = Pack(8);
        var start = ModBisect.Begin(pack);

        Assert.Equal(8, start.Off.Count);
        Assert.All(pack, mod => Assert.False(ModBisect.ShouldBeOn(start, mod)));
    }

    /// <summary>
    /// A crash that stops happening on its own, which is the one thing halving cannot survive.
    ///
    /// Every launch comes back clean, so every step concludes "it was in the half I switched off"
    /// and the hunt narrows just as fast as a real one — onto a mod that has done nothing. There
    /// is no arrangement of launches that tells this apart from a real find, because the two look
    /// identical from in here.
    ///
    /// So it is not hidden: the state counts how often the crash actually came back, and a hunt
    /// that never saw it again says so. The answer still gets given, because it is still the most
    /// likely mod — but as something to try rather than something proved.
    /// </summary>
    [Fact]
    public void A_crash_that_never_comes_back_is_answered_but_not_claimed()
    {
        var pack = Pack(20);
        var (verdict, state) = (BisectVerdict.Narrowing, ModBisect.Begin(pack));

        while (verdict == BisectVerdict.Narrowing) (verdict, state) = ModBisect.Next(state, crashed: false);

        Assert.Equal(BisectVerdict.Found, verdict);
        Assert.NotNull(ModBisect.Culprit(state));

        // And the thing that lets the screen hedge rather than accuse.
        Assert.False(state.Reproduced);
        Assert.Equal(0, state.Crashes);
    }

    /// <summary>A real hunt sees the crash again, which is what makes its answer worth stating.</summary>
    [Fact]
    public void A_real_hunt_sees_the_crash_come_back()
    {
        var pack = Pack(64);

        // mod064 is last, so every halving leaves it switched on and crashing.
        var (verdict, state) = (BisectVerdict.Narrowing, ModBisect.Begin(pack));

        while (verdict == BisectVerdict.Narrowing)
            (verdict, state) = ModBisect.Next(state, ModBisect.ShouldBeOn(state, "mod064.jar"));

        Assert.Equal(BisectVerdict.Found, verdict);
        Assert.Equal("mod064.jar", ModBisect.Culprit(state));
        Assert.True(state.Reproduced);
    }

    // ---- and it never loses anybody's mods ----

    /// <summary>
    /// The list to put back is fixed at the start and never edited. Everything else is worked out
    /// against it, so an interrupted bisect is a record of what to restore rather than a folder
    /// nobody can reconstruct.
    /// </summary>
    [Fact]
    public void What_to_restore_is_the_same_at_every_step()
    {
        var pack = Pack(30);
        var (verdict, state) = (BisectVerdict.Narrowing, ModBisect.Begin(pack));

        while (verdict == BisectVerdict.Narrowing)
        {
            Assert.Equal(pack.Order(StringComparer.OrdinalIgnoreCase), state.Restore);

            (verdict, state) = ModBisect.Next(state, ModBisect.ShouldBeOn(state, "mod017.jar"));
        }

        Assert.Equal(pack.Order(StringComparer.OrdinalIgnoreCase), state.Restore);
    }

    /// <summary>
    /// A mod that was already switched off before any of this started is not a suspect and is
    /// never switched back on — it was not running when the game crashed.
    /// </summary>
    [Fact]
    public void A_mod_that_was_already_off_stays_out_of_it()
    {
        var state = ModBisect.Begin(["a.jar", "b.jar"]);

        Assert.False(ModBisect.ShouldBeOn(state, "already-disabled.jar"));
        Assert.DoesNotContain("already-disabled.jar", state.Suspects);
    }

    /// <summary>
    /// Applying the same step twice, which is what a launch that was cancelled and started again
    /// does. What should be on is read off the state, so it lands in the same place.
    /// </summary>
    [Fact]
    public void Applying_a_step_twice_is_the_same_as_applying_it_once()
    {
        var (_, state) = ModBisect.Next(ModBisect.Begin(Pack(16)), crashed: false);

        var once = Pack(16).Where(mod => ModBisect.ShouldBeOn(state, mod)).ToList();
        var twice = Pack(16).Where(mod => ModBisect.ShouldBeOn(state, mod)).ToList();

        Assert.Equal(once, twice);
    }

    // ---- odds and ends ----

    [Fact]
    public void An_instance_with_no_mods_has_nothing_to_hunt()
    {
        var (verdict, state) = ModBisect.Next(ModBisect.Begin([]), crashed: true);

        Assert.Equal(BisectVerdict.NotAMod, verdict);
        Assert.Empty(state.Suspects);
    }

    [Fact]
    public void One_mod_is_settled_by_the_first_launch()
    {
        var (verdict, found, launches) = Hunt(["only.jar"], "only.jar");

        Assert.Equal(BisectVerdict.Found, verdict);
        Assert.Equal("only.jar", found);
        Assert.Equal(1, launches);
    }

    /// <summary>The same jar listed twice is one mod, not two rounds of hunting.</summary>
    [Fact]
    public void A_duplicate_in_the_list_is_one_suspect()
    {
        Assert.Single(ModBisect.Begin(["dup.jar", "dup.jar", "DUP.jar"]).Suspects);
    }
}
