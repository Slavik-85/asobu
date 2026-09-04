using System.IO.Compression;

namespace Asobu.Core.Skins;

/// <summary>
/// Wearing a skin without Mojang, by replacing the one the game falls back to.
///
/// An offline account has no profile for the game to fetch a skin from, so it draws the default
/// player instead — and the default player is just a texture in the jar, which a resource pack
/// can replace. That is the old trick of editing steve.png, done the way the game supports.
///
/// Two things make it more than a single file. The path moved: before 1.20 the defaults were
/// entity/steve.png and entity/alex.png, and since then they live under entity/player/wide and
/// entity/player/slim with nine names apiece. And which of those nine a player gets is decided by
/// hashing their uuid, so there is no way to know in advance which one to replace — all of them
/// are, and whichever the game reaches for is this skin.
/// </summary>
public static class SkinPack
{
    public const string FileName = "asobu-skin.zip";

    /// <summary>The nine the game chooses between, since 1.20.</summary>
    private static readonly string[] Defaults =
        ["alex", "ari", "efe", "kai", "makena", "noor", "steve", "sunny", "zuri"];

    /// <summary>Writes the pack into an instance, replacing any pack already written there.</summary>
    public static string Write(string gameDir, byte[] png, string minecraftVersion)
    {
        SkinPng.Validate(png);

        var folder = Path.Combine(gameDir, "resourcepacks");
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, FileName);

        // Built beside the target and moved into place, so a pack the game is reading is never
        // half a file — resource packs are opened while the game runs.
        var building = path + ".building";

        using (var stream = File.Create(building))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Add(zip, "pack.mcmeta", System.Text.Encoding.UTF8.GetBytes(Manifest(minecraftVersion)));

            // Both layouts, because one instance may be 1.19 and the next 1.21, and a path that
            // does not exist in a version is simply ignored by it.
            Add(zip, "assets/minecraft/textures/entity/steve.png", png);
            Add(zip, "assets/minecraft/textures/entity/alex.png", png);

            foreach (var arms in new[] { "wide", "slim" })
            foreach (var name in Defaults)
                Add(zip, $"assets/minecraft/textures/entity/player/{arms}/{name}.png", png);
        }

        File.Move(building, path, overwrite: true);

        return path;

        static void Add(ZipArchive zip, string name, byte[] bytes)
        {
            using var entry = zip.CreateEntry(name).Open();
            entry.Write(bytes);
        }
    }

    public static void Remove(string gameDir)
    {
        try
        {
            var path = Path.Combine(gameDir, "resourcepacks", FileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // In use by a running game. It will go next time.
        }
    }

    /// <summary>
    /// Switches the pack on in the instance's own settings.
    ///
    /// options.txt is the game's file and is rewritten wholesale every time it closes, so this
    /// only ever touches the one line — and adds it if the file has not been written yet, which
    /// is the case for an instance nobody has launched.
    /// </summary>
    public static void Enable(string gameDir)
    {
        var path = Path.Combine(gameDir, "options.txt");
        var entry = $"\"file/{FileName}\"";

        try
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
            var at = lines.FindIndex(line => line.StartsWith("resourcePacks:", StringComparison.Ordinal));

            if (at < 0)
            {
                lines.Add($"resourcePacks:[\"vanilla\",{entry}]");
            }
            else
            {
                if (lines[at].Contains(entry, StringComparison.Ordinal)) return;

                // Last, so it wins over anything else the person has enabled — it is the skin
                // they just chose, and they chose it after those.
                var open = lines[at].LastIndexOf(']');
                lines[at] = open < 0
                    ? $"resourcePacks:[\"vanilla\",{entry}]"
                    : lines[at][..open].TrimEnd() is var head && head.EndsWith('[')
                        ? head + entry + "]"
                        : head + "," + entry + "]";
            }

            Directory.CreateDirectory(gameDir);
            File.WriteAllLines(path, lines);
        }
        catch (IOException)
        {
            // The game has it open. The pack is on disk either way and can be turned on in game.
        }
    }

    /// <summary>Whether an instance is already wearing a pack from here.</summary>
    public static bool IsWorn(string gameDir) =>
        File.Exists(Path.Combine(gameDir, "resourcepacks", FileName));

    /// <summary>
    /// The pack.mcmeta.
    ///
    /// pack_format has changed almost every release and a wrong one gets the pack filed under
    /// "incompatible", so the range is given as well — newer versions read that and accept
    /// anything inside it, and older ones ignore what they do not know and go by the number.
    /// </summary>
    private static string Manifest(string minecraftVersion) =>
        $$"""
        {
          "pack": {
            "pack_format": {{FormatFor(minecraftVersion)}},
            "supported_formats": [1, 99],
            "description": "Your Asobu skin"
          }
        }
        """;

    /// <summary>
    /// The pack format a version expects. Only the boundaries that matter are listed; anything
    /// newer than the last one gets the last one, which the range above then rescues.
    /// </summary>
    private static int FormatFor(string version)
    {
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2 || !int.TryParse(parts[0], out var major)) return 34;
        if (!int.TryParse(parts[1], out var minor)) return 34;

        var patch = parts.Length > 2 && int.TryParse(parts[2].Split('-')[0], out var p) ? p : 0;

        if (major != 1) return 34;

        return minor switch
        {
            <= 12 => 3,
            13 or 14 => 4,
            15 => 5,
            16 => 6,
            17 => 7,
            18 => 8,
            19 => patch >= 4 ? 13 : patch >= 3 ? 12 : 9,
            20 => patch >= 5 ? 32 : patch >= 3 ? 22 : patch >= 2 ? 18 : 15,
            _ => 34,
        };
    }
}
