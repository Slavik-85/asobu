using System.Reflection;
using Asobu.Core;
using Asobu.Core.Mods;

namespace Asobu.Core.Tests;

/// <summary>
/// Taking out the build that was there once its replacement has landed.
///
/// Worth its own tests because the failure mode is deleting something somebody wanted: the file
/// just downloaded, or — far worse — a world they had been building in.
/// </summary>
public class ReplaceOnInstallTests : IDisposable
{
    private readonly string _folder =
        Directory.CreateTempSubdirectory("asobu-replace-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static readonly MethodInfo Retire =
        typeof(AsobuLauncher).GetMethod("RetirePreviousCopy", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static void Run(ModEntry? previous, string landed, ModKind kind) =>
        Retire.Invoke(null, [previous, landed, kind]);

    private string File_(string name, string content = "jar")
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static ModEntry Entry(string path) =>
        new(path, Path.GetFileName(path), "Sodium", "", "sodium", 3, true, null);

    [Fact]
    public void TakesOutTheBuildThatWasThere()
    {
        var old = File_("sodium-0.9.1.jar");
        var landed = File_("sodium-0.9.2.jar");

        Run(Entry(old), landed, ModKind.Mod);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(landed));
    }

    [Fact]
    public void NeverDeletesAWorld()
    {
        // A world is a folder full of somebody's building. "You already have this one" is not a
        // reason to remove it, and getting this wrong is unrecoverable.
        var world = Path.Combine(_folder, "My Survival World");
        Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, "level.dat"), "precious");

        Run(Entry(world), Path.Combine(_folder, "another-world"), ModKind.World);

        Assert.True(File.Exists(Path.Combine(world, "level.dat")));
    }

    [Fact]
    public void DoesNotDeleteTheFileThatWasJustDownloaded()
    {
        // Reinstalling the same build downloads over itself: previous and landed are one file,
        // and retiring it would leave the instance with nothing.
        var same = File_("sodium-0.9.2.jar");

        Run(Entry(same), same, ModKind.Mod);

        Assert.True(File.Exists(same));
    }

    [Fact]
    public void SurvivesAFileThatIsAlreadyGone()
    {
        var missing = Path.Combine(_folder, "removed-by-hand.jar");
        var landed = File_("sodium-0.9.2.jar");

        Run(Entry(missing), landed, ModKind.Mod);

        Assert.True(File.Exists(landed));
    }

    [Fact]
    public void NeverReachesOutOfTheFolderBeingInstalledInto()
    {
        // The copy is looked up across every content folder, so a project shipping both a mod and
        // a resource pack under one name can match the jar in mods/ while the pack is going into
        // resourcepacks/. Retiring that would delete a mod nobody touched.
        var mods = Directory.CreateDirectory(Path.Combine(_folder, "mods")).FullName;
        var packs = Directory.CreateDirectory(Path.Combine(_folder, "resourcepacks")).FullName;

        var theMod = Path.Combine(mods, "faithful-1.21.jar");
        File.WriteAllText(theMod, "a mod nobody touched");

        var landed = Path.Combine(packs, "Faithful-32x-1.21.zip");
        File.WriteAllText(landed, "the pack being installed");

        Run(Entry(theMod), landed, ModKind.ResourcePack);

        Assert.True(File.Exists(theMod));
        Assert.True(File.Exists(landed));
    }

    [Fact]
    public void DoesNothingWhenThereWasNoPreviousCopy()
    {
        var landed = File_("sodium-0.9.2.jar");

        Run(null, landed, ModKind.Mod);

        Assert.True(File.Exists(landed));
    }

    [Fact]
    public void TakesOutABuildThatHadBeenTurnedOff()
    {
        // A disabled jar is still a second copy once the new one lands, and the loader counts it
        // as clutter rather than as absent.
        var off = File_("sodium-0.9.1.jar.disabled");
        var landed = File_("sodium-0.9.2.jar");

        Run(new ModEntry(off, "sodium-0.9.1.jar", "Sodium", "", "sodium", 3, false, null), landed, ModKind.Mod);

        Assert.False(File.Exists(off));

        // The replacement is there under the switched-off name. This used to expect it at the
        // plain one, which was the bug rather than the intent: taking out the old copy is this
        // test's point, and updating a mod was never meant to also turn it on.
        Assert.True(File.Exists(landed + ".disabled"));
    }

    // ---- a mod that was switched off stays switched off ----

    private static ModEntry Switched(string path, bool on) =>
        new(path, Path.GetFileName(path), "Sodium", "", "sodium", 3, on, null);

    /// <summary>
    /// Updating is a newer copy of a mod, not a decision to start running it. Somebody who turned
    /// a mod off to stop it crashing their game, and then updated everything, was having it
    /// quietly turned back on — and finding out by crashing again.
    /// </summary>
    [Fact]
    public void An_update_leaves_a_disabled_mod_disabled()
    {
        var old = File_("sodium-1.0.jar.disabled");
        var landed = File_("sodium-2.0.jar");

        Run(Switched(old, on: false), landed, ModKind.Mod);

        Assert.False(File.Exists(old), "the build being replaced is still there");
        Assert.False(File.Exists(landed), "the new build was left switched on");
        Assert.True(File.Exists(landed + ".disabled"), "the new build is not switched off");
    }

    [Fact]
    public void An_update_leaves_an_enabled_mod_enabled()
    {
        var old = File_("sodium-1.0.jar");
        var landed = File_("sodium-2.0.jar");

        Run(Switched(old, on: true), landed, ModKind.Mod);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(landed), "an enabled mod came back switched off");
        Assert.False(File.Exists(landed + ".disabled"));
    }

    /// <summary>
    /// A leftover of the same name from an earlier build would make the rename fail, and failing
    /// it silently would leave the mod running when it was meant to be off.
    /// </summary>
    [Fact]
    public void Switching_off_survives_a_leftover_of_the_same_name()
    {
        var old = File_("sodium-1.0.jar.disabled");
        var stale = File_("sodium-2.0.jar.disabled", "an older attempt");
        var landed = File_("sodium-2.0.jar", "the new one");

        Run(Switched(old, on: false), landed, ModKind.Mod);

        Assert.True(File.Exists(stale));
        Assert.Equal("the new one", File.ReadAllText(stale));
    }

    /// <summary>A world is somebody's building. Nothing here renames or deletes one.</summary>
    [Fact]
    public void A_disabled_world_is_left_completely_alone()
    {
        var old = File_("myworld.disabled");
        var landed = File_("myworld-2.zip");

        Run(Switched(old, on: false), landed, ModKind.World);

        Assert.True(File.Exists(old));
        Assert.True(File.Exists(landed));
        Assert.False(File.Exists(landed + ".disabled"));
    }
}
