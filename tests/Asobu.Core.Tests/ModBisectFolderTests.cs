using Asobu.Core.Mods;

namespace Asobu.Core.Tests;

/// <summary>
/// The half of a bisect that renames somebody's jars.
///
/// The arithmetic is tested next door without a disk. This is the part that can actually lose
/// somebody's modpack, so it is played out against a real folder: a whole hunt, from every mod
/// switched off to the culprit found, and then the check that matters more than finding it — that
/// the folder can always be put back exactly as it was.
/// </summary>
public class ModBisectFolderTests : IDisposable
{
    private readonly string _mods = Directory.CreateTempSubdirectory("asobu-bisect-").FullName;

    public void Dispose() => Directory.Delete(_mods, recursive: true);

    private void Given(params string[] fileNames)
    {
        foreach (var name in fileNames) File.WriteAllText(Path.Combine(_mods, name), "not really a jar");
    }

    private IReadOnlyList<ModEntry> Scan() => ModScanner.Scan(_mods);

    private List<string> EnabledNow() =>
        [.. Scan().Where(mod => mod.Enabled).Select(mod => mod.FileName).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Plays the hunt out against the folder, crashing whenever the culprit is switched on.</summary>
    private (BisectVerdict Verdict, BisectState State) Hunt(string culprit)
    {
        var state = ModBisect.Begin(EnabledNow());
        ModBisect.Apply(state, Scan());

        var verdict = BisectVerdict.Narrowing;

        while (verdict == BisectVerdict.Narrowing && state.Launches < 20)
        {
            var crashed = EnabledNow().Contains(culprit, StringComparer.OrdinalIgnoreCase);

            (verdict, state) = ModBisect.Next(state, crashed);
            ModBisect.Apply(state, Scan());
        }

        return (verdict, state);
    }

    // ---- switching things off ----

    [Fact]
    public void The_first_step_switches_every_mod_off()
    {
        Given("a.jar", "b.jar", "c.jar");

        ModBisect.Apply(ModBisect.Begin(EnabledNow()), Scan());

        Assert.Empty(EnabledNow());
        Assert.Equal(3, Scan().Count);
    }

    /// <summary>A jar is only renamed, so nothing is ever lost — and it comes back under its own name.</summary>
    [Fact]
    public void Nothing_is_deleted_only_renamed()
    {
        Given("a.jar", "b.jar");

        var start = ModBisect.Begin(EnabledNow());
        ModBisect.Apply(start, Scan());

        Assert.Equal(2, Directory.GetFiles(_mods).Length);
        Assert.All(Directory.GetFiles(_mods), file => Assert.EndsWith(".jar.disabled", file));

        ModBisect.Restore(start, Scan());

        Assert.Equal(["a.jar", "b.jar"], EnabledNow());
    }

    // ---- and finding the one ----

    [Fact]
    public void A_whole_hunt_ends_with_the_culprit_off_and_everything_else_on()
    {
        Given("alpha.jar", "beta.jar", "gamma.jar", "delta.jar", "epsilon.jar", "zeta.jar", "eta.jar");

        var (verdict, state) = Hunt("delta.jar");

        Assert.Equal(BisectVerdict.Found, verdict);
        Assert.Equal("delta.jar", ModBisect.Culprit(state));

        // The state somebody actually wants to be left in: playable, without the one mod.
        Assert.Equal(["alpha.jar", "beta.jar", "epsilon.jar", "eta.jar", "gamma.jar", "zeta.jar"], EnabledNow());
    }

    /// <summary>
    /// A crash nothing in the folder is causing. Every mod is off and it still happens, so the
    /// hunt stops and hands the folder back untouched rather than blaming whatever is left.
    /// </summary>
    [Fact]
    public void A_crash_no_mod_causes_gives_every_mod_back()
    {
        Given("a.jar", "b.jar", "c.jar", "d.jar");

        var start = ModBisect.Begin(EnabledNow());
        ModBisect.Apply(start, Scan());

        var (verdict, state) = ModBisect.Next(start, crashed: true);
        ModBisect.Apply(state, Scan());

        Assert.Equal(BisectVerdict.NotAMod, verdict);
        Assert.Equal(["a.jar", "b.jar", "c.jar", "d.jar"], EnabledNow());
    }

    // ---- and always being able to put it back ----

    /// <summary>
    /// Stopping halfway, which is what somebody does when they get bored of relaunching. It has
    /// to work from the state alone, because by then the folder is in no state to be read for
    /// what it used to look like.
    /// </summary>
    [Fact]
    public void Stopping_halfway_puts_everything_back()
    {
        Given("a.jar", "b.jar", "c.jar", "d.jar", "e.jar", "f.jar");

        var state = ModBisect.Begin(EnabledNow());
        ModBisect.Apply(state, Scan());

        (_, state) = ModBisect.Next(state, crashed: false);
        ModBisect.Apply(state, Scan());

        Assert.NotEqual(6, EnabledNow().Count);

        ModBisect.Restore(state, Scan());

        Assert.Equal(["a.jar", "b.jar", "c.jar", "d.jar", "e.jar", "f.jar"], EnabledNow());
    }

    /// <summary>
    /// A mod somebody had already switched off before any of this. It was not running when the
    /// game crashed, so it is not a suspect — and putting things back must not switch it on.
    /// </summary>
    [Fact]
    public void A_mod_that_was_already_off_is_still_off_afterwards()
    {
        Given("on.jar", "other.jar");
        File.Move(Path.Combine(_mods, "other.jar"), Path.Combine(_mods, "other.jar.disabled"));

        var state = ModBisect.Begin(EnabledNow());
        ModBisect.Apply(state, Scan());
        ModBisect.Restore(state, Scan());

        Assert.Equal(["on.jar"], EnabledNow());
    }

    /// <summary>Applying the same step again renames nothing, which is what makes a repeat safe.</summary>
    [Fact]
    public void Applying_a_step_that_is_already_applied_changes_nothing()
    {
        Given("a.jar", "b.jar", "c.jar", "d.jar");

        var state = ModBisect.Begin(EnabledNow());

        Assert.Equal(4, ModBisect.Apply(state, Scan()).Changed);
        Assert.Equal(0, ModBisect.Apply(state, Scan()).Changed);
    }

    /// <summary>
    /// A jar and its own .disabled twin in one folder, which is what re-downloading a mod that
    /// was switched off leaves behind.
    ///
    /// The scanner strips the suffix, so both come back under one name — and renaming either onto
    /// the other destroys a file. Neither is touched, and the fact that they were not is reported
    /// rather than passed over, because a hunt that quietly does nothing to a mod is a hunt whose
    /// answer is wrong.
    /// </summary>
    [Fact]
    public void A_jar_that_is_in_the_folder_twice_is_left_alone_and_reported()
    {
        Given("twin.jar", "safe.jar");
        File.Copy(Path.Combine(_mods, "twin.jar"), Path.Combine(_mods, "twin.jar.disabled"));

        var state = ModBisect.Begin(["twin.jar", "safe.jar"]);
        var change = ModBisect.Apply(state, Scan());

        // Both copies still there, neither renamed onto the other.
        Assert.True(File.Exists(Path.Combine(_mods, "twin.jar")));
        Assert.True(File.Exists(Path.Combine(_mods, "twin.jar.disabled")));

        Assert.Contains("twin.jar", change.Refused);
        Assert.False(change.Complete);

        // And the rest of the folder was still switched over.
        Assert.False(File.Exists(Path.Combine(_mods, "safe.jar")));
    }

    /// <summary>
    /// A mod installed while a hunt was running. It was not there when the hunt started, so it
    /// is none of the hunt's business — switching it off would leave it off for good, since
    /// putting things back only knows about the mods that were there at the beginning.
    /// </summary>
    [Fact]
    public void A_mod_installed_midway_is_never_touched()
    {
        Given("a.jar", "b.jar");

        var state = ModBisect.Begin(EnabledNow());
        ModBisect.Apply(state, Scan());

        Given("arrived-later.jar");

        ModBisect.Apply(state, Scan());
        Assert.Contains("arrived-later.jar", EnabledNow());

        ModBisect.Restore(state, Scan());
        Assert.Equal(["a.jar", "arrived-later.jar", "b.jar"], EnabledNow());
    }

    /// <summary>
    /// One jar that will not move must not take the rest with it. Stopping at the first failure
    /// would leave every mod after it in whatever state the last step left — and those are the
    /// ones nobody would ever get back.
    /// </summary>
    [Fact]
    public void One_jar_that_will_not_move_does_not_abandon_the_others()
    {
        Given("first.jar", "locked.jar", "last.jar");

        var state = ModBisect.Begin(EnabledNow());

        // Held open with no sharing, which is what an antivirus or a syncing client does.
        using (var _ = new FileStream(Path.Combine(_mods, "locked.jar"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var change = ModBisect.Apply(state, Scan());

            Assert.Contains("locked.jar", change.Refused);
            Assert.Equal(2, change.Changed);
        }

        // The two that could move did, and the hunt can still put everything back afterwards.
        Assert.DoesNotContain("first.jar", EnabledNow());

        ModBisect.Restore(state, Scan());
        Assert.Equal(["first.jar", "last.jar", "locked.jar"], EnabledNow());
    }

    /// <summary>
    /// A jar deleted from the folder while the hunt was running. It cannot be switched back on,
    /// and that must not stop everything else from being.
    /// </summary>
    [Fact]
    public void A_mod_that_vanished_midway_does_not_strand_the_rest()
    {
        Given("a.jar", "b.jar", "c.jar");

        var state = ModBisect.Begin(EnabledNow());
        ModBisect.Apply(state, Scan());

        File.Delete(Path.Combine(_mods, "b.jar.disabled"));

        ModBisect.Restore(state, Scan());

        Assert.Equal(["a.jar", "c.jar"], EnabledNow());
    }
}
