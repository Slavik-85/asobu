using System.IO.Compression;
using System.Text.Json;
using Asobu.Core.Skins;

namespace Asobu.Core.Tests;

/// <summary>
/// Wearing a skin without Mojang.
///
/// An offline account has no profile to fetch a skin from, so the game draws the default player
/// — and a resource pack can replace that. Which default it draws is decided by hashing the uuid,
/// so the pack has to replace every one of them or it is a coin toss whether the skin shows.
/// </summary>
public class SkinPackTests : IDisposable
{
    private readonly string _game = Directory.CreateTempSubdirectory("asobu-pack-").FullName;

    public void Dispose() => Directory.Delete(_game, recursive: true);

    private static byte[] Png()
    {
        var bytes = new byte[64];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        signature.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 64);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 64);

        return bytes;
    }

    private List<string> Entries(string version = "1.20.1")
    {
        SkinPack.Write(_game, Png(), version);

        using var zip = ZipFile.OpenRead(Path.Combine(_game, "resourcepacks", SkinPack.FileName));

        return [.. zip.Entries.Select(e => e.FullName)];
    }

    // ---- what the pack has to contain ----

    /// <summary>
    /// All nine, both arm widths. Replacing only steve.png leaves it to chance: a uuid that
    /// hashes to "zuri" would still show the default.
    /// </summary>
    [Fact]
    public void Every_default_the_game_might_pick_is_replaced()
    {
        var entries = Entries();

        foreach (var name in new[] { "alex", "ari", "efe", "kai", "makena", "noor", "steve", "sunny", "zuri" })
        {
            Assert.Contains($"assets/minecraft/textures/entity/player/wide/{name}.png", entries);
            Assert.Contains($"assets/minecraft/textures/entity/player/slim/{name}.png", entries);
        }
    }

    /// <summary>Where the defaults lived before 1.20, so one pack covers every instance.</summary>
    [Fact]
    public void The_pre_1_20_paths_are_there_too()
    {
        var entries = Entries();

        Assert.Contains("assets/minecraft/textures/entity/steve.png", entries);
        Assert.Contains("assets/minecraft/textures/entity/alex.png", entries);
    }

    [Fact]
    public void The_skin_written_in_is_the_skin_that_comes_out()
    {
        SkinPack.Write(_game, Png(), "1.20.1");

        using var zip = ZipFile.OpenRead(Path.Combine(_game, "resourcepacks", SkinPack.FileName));
        using var entry = zip.GetEntry("assets/minecraft/textures/entity/player/wide/steve.png")!.Open();
        using var read = new MemoryStream();

        entry.CopyTo(read);

        Assert.Equal(Png(), read.ToArray());
    }

    /// <summary>A pack the game files under "incompatible" is a pack nobody sees.</summary>
    [Fact]
    public void The_manifest_declares_a_format_and_a_range()
    {
        SkinPack.Write(_game, Png(), "1.19.2");

        using var zip = ZipFile.OpenRead(Path.Combine(_game, "resourcepacks", SkinPack.FileName));
        using var entry = zip.GetEntry("pack.mcmeta")!.Open();

        var pack = JsonDocument.Parse(entry).RootElement.GetProperty("pack");

        Assert.Equal(9, pack.GetProperty("pack_format").GetInt32());
        Assert.Equal(2, pack.GetProperty("supported_formats").GetArrayLength());
    }

    [Fact]
    public void Writing_it_again_replaces_it_rather_than_piling_up()
    {
        SkinPack.Write(_game, Png(), "1.20.1");
        SkinPack.Write(_game, Png(), "1.20.1");

        Assert.Single(Directory.GetFiles(Path.Combine(_game, "resourcepacks")));
    }

    // ---- turning it on ----

    /// <summary>An instance nobody has launched has no options.txt to edit.</summary>
    [Fact]
    public void It_is_switched_on_even_before_the_game_has_ever_run()
    {
        SkinPack.Enable(_game);

        Assert.Contains($"file/{SkinPack.FileName}", File.ReadAllText(Path.Combine(_game, "options.txt")));
    }

    [Fact]
    public void An_existing_setting_keeps_the_packs_already_in_it()
    {
        var options = Path.Combine(_game, "options.txt");
        File.WriteAllLines(options, ["fov:0.0", "resourcePacks:[\"vanilla\",\"file/faithful.zip\"]", "lang:en_gb"]);

        SkinPack.Enable(_game);

        var line = File.ReadAllLines(options).Single(l => l.StartsWith("resourcePacks:"));

        Assert.Contains("faithful.zip", line);
        Assert.Contains($"file/{SkinPack.FileName}", line);
        Assert.Contains("fov:0.0", File.ReadAllText(options));
    }

    /// <summary>Wearing two skins in a row must not list the pack twice.</summary>
    [Fact]
    public void Switching_it_on_twice_lists_it_once()
    {
        SkinPack.Enable(_game);
        SkinPack.Enable(_game);

        var line = File.ReadAllLines(Path.Combine(_game, "options.txt")).Single(l => l.StartsWith("resourcePacks:"));

        Assert.Equal(1, line.Split(SkinPack.FileName).Length - 1);
    }

    [Fact]
    public void An_instance_says_whether_it_is_wearing_one()
    {
        Assert.False(SkinPack.IsWorn(_game));

        SkinPack.Write(_game, Png(), "1.20.1");
        Assert.True(SkinPack.IsWorn(_game));

        SkinPack.Remove(_game);
        Assert.False(SkinPack.IsWorn(_game));
    }
}
