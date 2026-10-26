using System.IO.Compression;
using Asobu.Core;
using Asobu.Core.Mods;

namespace Asobu.Core.Tests;

/// <summary>
/// An archive dropped on an instance can come from anywhere, so these are the ways one could be
/// built to misbehave: escaping saves, crashing the unpacker, or passing for something it isn't.
/// </summary>
public sealed class DroppedArchiveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("asobu-drop").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Saves => Directory.CreateDirectory(Path.Combine(_dir, "saves")).FullName;

    private string Zip(string name, params string[] entries)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (var entry in entries)
            using (var writer = new StreamWriter(zip.CreateEntry(entry).Open()))
                writer.Write("x");

        return path;
    }

    [Fact]
    public void KeepsADriveNamedWrapperInsideSaves()
    {
        var world = AsobuLauncher.UnpackWorld(Zip("crafted.zip", "C:/level.dat"), Saves, "crafted.zip");

        Assert.Equal("crafted", world);
        Assert.True(File.Exists(Path.Combine(Saves, "crafted", "level.dat")));
    }

    [Fact]
    public void RefusesANulInAnEntryWithoutLeavingHalfAWorld()
    {
        var world = AsobuLauncher.UnpackWorld(Zip("nul.zip", "w/level.dat", "w/bad\0name"), Saves, "nul.zip");

        Assert.Null(world);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Saves));
    }

    [Fact]
    public void TakesAModpackForAModpackRatherThanTheWorldInsideIt()
    {
        var pack = Zip("pack.zip", "manifest.json", "overrides/saves/w/level.dat", "overrides/resourcepacks/r/assets/a.png");

        Assert.Equal(ModKind.Modpack, ModScanner.SniffKind(pack));
    }
}
