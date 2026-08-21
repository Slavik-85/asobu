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
        Assert.True(File.Exists(landed));
    }
}
