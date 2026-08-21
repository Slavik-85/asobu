using System.IO.Compression;
using System.Text.Json;

namespace Asobu.Core.Mods;

/// <summary>One jar in an instance's mods folder, with whatever metadata it declares.</summary>
public sealed record ModEntry(
    string Path,
    string FileName,
    string Name,
    string Author,
    string? ModId,
    long Size,
    bool Enabled,
    byte[]? IconPng)
{
    public string SizeLabel => Format.Bytes(Size);
}

/// <summary>
/// Reads what an instance has installed — mods, resource packs, shaders, data packs or worlds.
/// Enabled/disabled is the usual ".disabled" suffix convention, which every other launcher
/// understands, so toggling here doesn't strand a pack elsewhere.
///
/// The five differ in what they are as much as where they live: mods and packs are files, worlds
/// are folders, and only a mod declares its own name and author. Everything else falls back to
/// the file name, which is what the person named it anyway.
/// </summary>
public static class ModScanner
{
    /// <summary>
    /// What every launcher marks a switched-off file with. Public because turning something
    /// off is not the only place it is needed: replacing a file has to put the replacement
    /// back into the state its predecessor was in, and one spelling of this is enough.
    /// </summary>
    public const string DisabledSuffix = ".disabled";

    /// <summary>
    /// What was inside each jar last time. Set once at startup; without it every scan opens
    /// every jar again, which is over a second for a folder of twenty-five on a cold disk.
    /// </summary>
    public static ModMetadataCache? Cache { get; set; }

    /// <summary>The mods folder of the instance living in <paramref name="instanceFolder"/>.</summary>
    public static string ModsDirectory(AsobuPaths paths, string instanceFolder) =>
        System.IO.Path.Combine(paths.InstanceGameDir(instanceFolder), "mods");

    /// <summary>
    /// Where a given kind of content belongs in that instance, or null for one that cannot be
    /// dropped in as a file. Only mods are ever scanned back out; the rest the game finds itself.
    /// </summary>
    public static string? ContentDirectory(AsobuPaths paths, string instanceFolder, ModKind kind) =>
        ModContent.FolderFor(kind) is { } folder
            ? System.IO.Path.Combine(paths.InstanceGameDir(instanceFolder), folder)
            : null;

    public static IReadOnlyList<ModEntry> Scan(string modsDirectory) => Scan(modsDirectory, ModKind.Mod);

    /// <summary>Everything of one kind installed in the folder it lives in.</summary>
    public static IReadOnlyList<ModEntry> Scan(string directory, ModKind kind)
    {
        if (!Directory.Exists(directory)) return [];

        var found = new List<ModEntry>();

        // Worlds are folders rather than files, and the folder is the world.
        if (kind == ModKind.World)
        {
            foreach (var folder in Directory.EnumerateDirectories(directory))
                if (ReadWorld(folder) is { } world)
                    found.Add(world);

            return [.. found.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)];
        }

        var extension = kind == ModKind.Mod ? ".jar" : ".zip";

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var enabled = file.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
            var disabled = file.EndsWith(extension + DisabledSuffix, StringComparison.OrdinalIgnoreCase);
            if (!enabled && !disabled) continue;

            found.Add(kind == ModKind.Mod ? Read(file, enabled) : ReadPack(file, enabled));
        }

        // A resource pack, shader or data pack can also be an unzipped folder, which is how
        // anyone editing one keeps it. Left out for mods, where a loose folder is not loadable.
        if (kind != ModKind.Mod)
            foreach (var folder in Directory.EnumerateDirectories(directory))
                found.Add(ReadPackFolder(folder));

        // One write per scan rather than one per jar, and nothing at all when a folder held no
        // surprises.
        Cache?.Save();

        return [.. found.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Dropped into a world's folder when the launcher installs it. Its presence is the only
    /// thing separating a downloaded world from one the player built, and worlds someone made
    /// themselves must never appear in a list that offers to delete or disable them.
    ///
    /// A dot-prefixed file, which Minecraft ignores and which travels with the world if it is
    /// copied elsewhere.
    /// </summary>
    public const string WorldMarker = ".asobu-world.json";

    /// <summary>
    /// A downloaded world: a folder holding a level.dat and our marker. Anything else in saves/
    /// is the player's own — their worlds, their backups — and is left well alone.
    /// </summary>
    private static ModEntry? ReadWorld(string folder)
    {
        if (!File.Exists(System.IO.Path.Combine(folder, "level.dat"))) return null;
        if (!File.Exists(System.IO.Path.Combine(folder, WorldMarker))) return null;

        var name = System.IO.Path.GetFileName(folder);
        var icon = TryReadFile(System.IO.Path.Combine(folder, "icon.png"));

        // The world's own display name lives in level.dat, which is gzipped NBT — a parser for
        // one string is not worth carrying, and the folder is what the person named it.
        return new ModEntry(folder, name, name, "", null, FolderSize(folder), true, icon);
    }

    /// <summary>Marks a freshly installed world as one the launcher put there.</summary>
    public static void MarkWorld(string folder, string source)
    {
        try
        {
            File.WriteAllText(
                System.IO.Path.Combine(folder, WorldMarker),
                $$"""{"installedBy":"Asobu","source":{{JsonSerializer.Serialize(source)}},"installed":{{JsonSerializer.Serialize(DateTimeOffset.UtcNow)}}}""");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unmarked, so it will not be listed. The world still works; it simply reads as one
            // of the player's own, which is the safe way to be wrong.
        }
    }

    /// <summary>A pack kept unzipped. Its metadata is the same, just not inside an archive.</summary>
    private static ModEntry ReadPackFolder(string folder)
    {
        var name = System.IO.Path.GetFileName(folder);
        var enabled = !name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
        if (!enabled) name = name[..^DisabledSuffix.Length];

        var description = ReadPackDescription(TryReadFile(System.IO.Path.Combine(folder, "pack.mcmeta")));

        return new ModEntry(
            folder, name, name, description, null, FolderSize(folder), enabled,
            TryReadFile(System.IO.Path.Combine(folder, "pack.png")));
    }

    /// <summary>
    /// A zipped resource pack, shader or data pack. Packs carry a description rather than an
    /// author — nothing in the format names one — so that is what the Creator column shows.
    /// </summary>
    private static ModEntry ReadPack(string path, bool enabled)
    {
        var info = new FileInfo(path);
        var fileName = System.IO.Path.GetFileName(path);
        if (!enabled) fileName = fileName[..^DisabledSuffix.Length];

        try
        {
            using var archive = ZipFile.OpenRead(path);

            var description = "";
            if (archive.GetEntry("pack.mcmeta") is { } meta)
            {
                using var stream = meta.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                description = ReadPackDescription(buffer.ToArray());
            }

            byte[]? icon = null;
            if (archive.GetEntry("pack.png") is { } image)
            {
                using var stream = image.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                icon = buffer.ToArray();
            }

            return new ModEntry(path, fileName, Clean(fileName), description, null, info.Length, enabled, icon);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            // A shader pack has no manifest at all, and a damaged zip still belongs in the list.
            return new ModEntry(path, fileName, Clean(fileName), "", null, info.Length, enabled, null);
        }
    }

    /// <summary>
    /// pack.mcmeta's description, which is either a string or the JSON-text object Minecraft
    /// also accepts. Anything else is not worth chasing — the row still has a name.
    /// </summary>
    private static ModEntry Remember(string key, ModEntry entry)
    {
        Cache?.Put(key, entry);
        return entry;
    }

    private static string ReadPackDescription(byte[]? mcmeta)
    {
        if (mcmeta is null or { Length: 0 }) return "";

        try
        {
            using var document = JsonDocument.Parse(mcmeta, new JsonDocumentOptions { AllowTrailingCommas = true });

            if (!document.RootElement.TryGetProperty("pack", out var pack)) return "";
            if (!pack.TryGetProperty("description", out var description)) return "";

            return description.ValueKind switch
            {
                JsonValueKind.String => Flatten(description.GetString()),
                JsonValueKind.Object when description.TryGetProperty("text", out var text) => Flatten(text.GetString()),
                _ => "",
            };
        }
        catch (JsonException)
        {
            return "";
        }

        // Pack descriptions carry newlines and colour codes; the column is one line.
        static string Flatten(string? text) =>
            text is null ? "" : System.Text.RegularExpressions.Regex.Replace(text, @"[\r\n]+|\u00A7.", " ").Trim();
    }

    private static byte[]? TryReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>What a folder takes up. Worlds are the reason this exists, and they can be large.</summary>
    private static long FolderSize(string folder)
    {
        try
        {
            return new DirectoryInfo(folder)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// One jar, read exactly as a folder scan reads each of its files. For refreshing a single
    /// row after its file has been replaced, where re-reading the whole folder would mean
    /// opening every other jar to learn nothing.
    /// </summary>
    public static ModEntry? ReadOne(string path)
    {
        if (!File.Exists(path)) return null;

        var enabled = path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
        var disabled = path.EndsWith(".jar" + DisabledSuffix, StringComparison.OrdinalIgnoreCase);

        return enabled || disabled ? Read(path, enabled) : null;
    }

    private static ModEntry Read(string path, bool enabled)
    {
        var info = new FileInfo(path);
        var fallbackName = System.IO.Path.GetFileName(path);
        if (!enabled) fallbackName = fallbackName[..^DisabledSuffix.Length];

        // Opening the jar is the expensive half, and nothing inside one changes while it sits
        // there. The file's own facts — where it is, how big, whether it is switched on — come
        // from the file either way.
        var key = ModMetadataCache.KeyFor(info);

        if (Cache is { } cache && cache.TryGet(key, out var remembered))
            return remembered with
            {
                Path = path,
                FileName = fallbackName,
                Size = info.Length,
                Enabled = enabled,
            };

        try
        {
            using var archive = ZipFile.OpenRead(path);

            var manifest = archive.GetEntry("fabric.mod.json") ?? archive.GetEntry("quilt.mod.json");

            // Forge and NeoForge declare themselves in TOML instead. That used to be dismissed as
            // not worth a parser, and the cost of dismissing it was every Forge mod in the list
            // showing as its own file name with no author — "cushionbackport-26.2-NeoF…" beside
            // "Fabric API", as though one of them were broken.
            if (manifest is null)
                return Remember(key, ReadForgeStyle(archive, path, fallbackName, info.Length, enabled)
                    ?? new ModEntry(
                        path, fallbackName, Clean(fallbackName), "Unknown", null, info.Length, enabled, null));

            using var stream = manifest.Open();
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = document.RootElement;

            // Quilt nests everything one level down under "quilt_loader".
            if (root.TryGetProperty("quilt_loader", out var quilt)) root = quilt;

            var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            var icon = ReadIcon(archive, root);

            // The declared mod id is what crash reports and mixin errors actually name, so it is
            // worth carrying even though nothing on screen shows it.
            var modId = root.TryGetProperty("id", out var i) ? i.GetString() : null;

            return Remember(key, new ModEntry(
                path,
                fallbackName,
                string.IsNullOrWhiteSpace(name) ? Clean(fallbackName) : name!,
                ReadAuthors(root),
                string.IsNullOrWhiteSpace(modId) ? null : modId,
                info.Length,
                enabled,
                icon));
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or IOException)
        {
            // A malformed or unreadable jar still belongs in the list — just without metadata.
            // Remembered too: re-opening a broken zip every scan costs the same as a good one.
            return Remember(key, new ModEntry(
                path, fallbackName, Clean(fallbackName), "Unknown", null, info.Length, enabled, null));
        }
    }

    private static string ReadAuthors(JsonElement root)
    {
        if (!root.TryGetProperty("authors", out var authors) || authors.ValueKind != JsonValueKind.Array)
            return "Unknown";

        var names = new List<string>();
        foreach (var author in authors.EnumerateArray())
        {
            // Entries are either a bare string or an object carrying a "name".
            var value = author.ValueKind switch
            {
                JsonValueKind.String => author.GetString(),
                JsonValueKind.Object when author.TryGetProperty("name", out var n) => n.GetString(),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(value)) names.Add(value!);
        }

        return names.Count == 0 ? "Unknown" : string.Join(", ", names);
    }

    /// <summary>
    /// A Forge or NeoForge mod, out of its own manifest, or null when it has none.
    ///
    /// NeoForge moved the file in 1.20.5 and both names are still in the wild, so both are tried.
    /// </summary>
    private static ModEntry? ReadForgeStyle(
        ZipArchive archive, string path, string fallbackName, long size, bool enabled)
    {
        var entry = archive.GetEntry("META-INF/neoforge.mods.toml")
                    ?? archive.GetEntry("META-INF/mods.toml");

        if (entry is null) return null;

        string text;
        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return null;
        }

        var (mod, top) = ReadTomlHeads(text);

        string? Field(string name) =>
            mod.TryGetValue(name, out var inMod) ? inMod
            : top.TryGetValue(name, out var atTop) ? atTop
            : null;

        var displayName = Field("displayName");
        var modId = Field("modId");

        // The logo is named relative to the jar root, same as Fabric's icon.
        var icon = Field("logoFile") is { Length: > 0 } logo ? ReadEntry(archive, logo) : null;

        return new ModEntry(
            path,
            fallbackName,
            string.IsNullOrWhiteSpace(displayName) ? Clean(fallbackName) : displayName!,
            Field("authors") is { Length: > 0 } authors ? authors : "Unknown",
            string.IsNullOrWhiteSpace(modId) ? null : modId,
            size,
            enabled,
            icon);
    }

    /// <summary>
    /// The top-level keys of a mods.toml and those of its first [[mods]] block.
    ///
    /// Enough of TOML to read a manifest and no more: keys and quoted values, the tables they sit
    /// under, and nothing else. A mod's own block wins over the top level, which is where a pack
    /// of several mods puts the things they share — the licence, and often the authors.
    ///
    /// Only the first [[mods]] block. A jar holding several is one file in the folder either way,
    /// and the first is the one it is named after.
    /// </summary>
    private static (Dictionary<string, string> Mod, Dictionary<string, string> Top) ReadTomlHeads(string text)
    {
        var top = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mod = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var inFirstMod = false;
        var pastFirstMod = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#') continue;

            if (line.StartsWith('['))
            {
                var isMods = line.StartsWith("[[mods]]", StringComparison.OrdinalIgnoreCase);

                if (isMods && pastFirstMod) inFirstMod = false;
                else if (isMods) { inFirstMod = true; pastFirstMod = true; }
                else inFirstMod = false;

                continue;
            }

            var split = line.IndexOf('=');
            if (split <= 0) continue;

            var value = Unquote(line[(split + 1)..].Trim());
            if (value.Length == 0) continue;

            (inFirstMod ? mod : top).TryAdd(line[..split].Trim(), value);
        }

        return (mod, top);
    }

    /// <summary>
    /// A TOML scalar as its text. Multi-line strings open with three quotes and carry nothing
    /// useful on that first line, so they come back empty rather than as a stray quote mark —
    /// descriptions are written that way and nothing here shows one.
    /// </summary>
    private static string Unquote(string value)
    {
        if (value.StartsWith("'''", StringComparison.Ordinal)
            || value.StartsWith("\"\"\"", StringComparison.Ordinal))
            return "";

        // Read to the closing quote rather than expecting the line to end there. Manifests are
        // written from Forge's own template, which puts a comment after almost every line —
        // modId = "deimos" #mandatory — so a value that "ends with a quote" describes hardly any
        // of them, and treating those as unquoted leaves the quotes in the name.
        if (value.Length >= 2 && value[0] is '"' or '\'')
        {
            var close = value.IndexOf(value[0], 1);
            if (close > 0) return value[1..close];
        }

        // Unquoted, so anything after a comment marker is not part of it.
        var hash = value.IndexOf('#');
        return (hash >= 0 ? value[..hash] : value).Trim();
    }

    private static byte[]? ReadEntry(ZipArchive archive, string name)
    {
        if (archive.GetEntry(name.TrimStart('/')) is not { } entry) return null;

        try
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return null;
        }
    }

    private static byte[]? ReadIcon(ZipArchive archive, JsonElement root)
    {
        if (!root.TryGetProperty("icon", out var icon) || icon.ValueKind != JsonValueKind.String) return null;
        if (archive.GetEntry(icon.GetString()!) is not { } entry) return null;

        try
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// What a file or folder actually is, read from what is inside it.
    ///
    /// Needed only when nobody said — adding something while the list is showing Everything.
    /// A .jar is a mod and nothing else, but a .zip could be any of four things, and the way to
    /// know is to look: a world has a level.dat, a shader pack a shaders folder, a resource pack
    /// assets, a data pack data. <see cref="ModKind.Any"/> when it is none of them.
    /// </summary>
    public static ModKind SniffKind(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                if (File.Exists(System.IO.Path.Combine(path, "level.dat"))) return ModKind.World;
                if (Directory.Exists(System.IO.Path.Combine(path, "shaders"))) return ModKind.Shader;
                if (Directory.Exists(System.IO.Path.Combine(path, "assets"))) return ModKind.ResourcePack;
                if (Directory.Exists(System.IO.Path.Combine(path, "data"))) return ModKind.DataPack;

                return ModKind.Any;
            }

            if (!File.Exists(path)) return ModKind.Any;

            if (path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) return ModKind.Mod;
            if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return ModKind.Any;

            using var archive = ZipFile.OpenRead(path);

            var names = archive.Entries
                .Select(entry => entry.FullName.Replace('\\', '/'))
                .ToList();

            // A wrapping folder is common in a downloaded world, so a match anywhere counts.
            bool Holds(string what) => names.Any(name =>
                name.Equals(what, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("/" + what, StringComparison.OrdinalIgnoreCase));

            bool HasFolder(string what) => names.Any(name =>
                name.StartsWith(what + "/", StringComparison.OrdinalIgnoreCase)
                || name.Contains("/" + what + "/", StringComparison.OrdinalIgnoreCase));

            if (Holds("level.dat")) return ModKind.World;
            if (HasFolder("shaders")) return ModKind.Shader;

            // Checked after shaders: a shader pack can carry an assets folder too, and its own
            // shaders folder is the more specific fact.
            if (HasFolder("assets")) return ModKind.ResourcePack;
            if (HasFolder("data")) return ModKind.DataPack;

            // A pack.mcmeta with nothing else recognisable is a resource pack more often than not.
            return Holds("pack.mcmeta") ? ModKind.ResourcePack : ModKind.Any;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ModKind.Any;
        }
    }

    /// <summary>Turns "sodium-fabric-0.5.8.jar" into something closer to a title.</summary>
    /// <summary>
    /// A readable name out of a file name, for the mods that never say what they are called.
    ///
    /// Most declare a name and this is never seen. Some declare none at all — Essential ships a
    /// manifest carrying an id and a version and nothing else, and its Forge jar has no manifest
    /// whatsoever — and for those this is the only name there will ever be. Left as the bare file
    /// name it read "Essential 1-4-1-1 fabric 26-2" in a column beside "Fabric API", which looks
    /// like the launcher failing rather than the jar being quiet.
    ///
    /// So the version and the platform come off and what is in front of them is the name. That is
    /// all a person does when they read one of these, and it is right far more often than it is
    /// wrong — the worst case is a name a little shorter than its author would have written.
    /// </summary>
    private static string Clean(string fileName)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);

        // Build metadata: everything after a + is the game version, never part of the name.
        if (stem.IndexOf('+') is > 0 and var plus) stem = stem[..plus];

        var pieces = stem.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries);
        var kept = pieces.TakeWhile(piece => !LooksLikeVersion(piece)).ToList();

        while (kept.Count > 1 && Platforms.Contains(kept[^1])) kept.RemoveAt(kept.Count - 1);

        // A name made entirely of version parts leaves nothing to show, so the file name stands.
        if (kept.Count == 0) return stem.Replace('_', ' ');

        var name = string.Join(' ', kept);
        return char.IsLower(name[0]) ? char.ToUpperInvariant(name[0]) + name[1..] : name;
    }

    /// <summary>"1.2.3", "v26.2.1", "mc1.21" — a segment that says which build rather than what.</summary>
    private static bool LooksLikeVersion(string piece) =>
        char.IsAsciiDigit(piece[0])
        || (piece.Length > 1 && piece[0] is 'v' or 'V' && char.IsAsciiDigit(piece[1]))
        || (piece.Length > 2 && piece.StartsWith("mc", StringComparison.OrdinalIgnoreCase)
            && char.IsAsciiDigit(piece[2]));

    /// <summary>What a jar's name says about where it runs rather than about what it is.</summary>
    private static readonly HashSet<string> Platforms = new(StringComparer.OrdinalIgnoreCase)
    {
        "fabric", "forge", "neoforge", "quilt", "mc", "client", "server", "universal", "all",
    };

    /// <summary>
    /// Flips something on or off by renaming it, and returns its new path. Packs kept unzipped
    /// are folders, and a folder is moved rather than copied — same rename, different call.
    /// </summary>
    public static string SetEnabled(ModEntry mod, bool enabled)
    {
        if (mod.Enabled == enabled) return mod.Path;

        var target = enabled
            ? mod.Path[..^DisabledSuffix.Length]
            : mod.Path + DisabledSuffix;

        if (Directory.Exists(mod.Path)) Directory.Move(mod.Path, target);
        else File.Move(mod.Path, target, overwrite: false);

        return target;
    }
}
