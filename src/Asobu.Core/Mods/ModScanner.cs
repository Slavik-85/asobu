using System.IO.Compression;
using System.Text.Json;

namespace Asobu.Core.Mods;

/// <summary>One jar in an instance's mods folder, with whatever metadata it declares.</summary>
public sealed record ModEntry(
    string Path,
    string FileName,
    string Name,
    string Author,
    long Size,
    bool Enabled,
    byte[]? IconPng)
{
    public string SizeLabel => Format.Bytes(Size);
}

/// <summary>
/// Reads an instance's mods folder. Enabled/disabled is the usual ".disabled" suffix convention,
/// which every other launcher understands, so toggling here doesn't strand a pack elsewhere.
/// </summary>
public static class ModScanner
{
    private const string DisabledSuffix = ".disabled";

    public static string ModsDirectory(AsobuPaths paths, string instanceId) =>
        System.IO.Path.Combine(paths.InstanceGameDir(instanceId), "mods");

    public static IReadOnlyList<ModEntry> Scan(string modsDirectory)
    {
        if (!Directory.Exists(modsDirectory)) return [];

        var mods = new List<ModEntry>();

        foreach (var file in Directory.EnumerateFiles(modsDirectory))
        {
            var enabled = file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
            var disabled = file.EndsWith(".jar" + DisabledSuffix, StringComparison.OrdinalIgnoreCase);
            if (!enabled && !disabled) continue;

            mods.Add(Read(file, enabled));
        }

        return [.. mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static ModEntry Read(string path, bool enabled)
    {
        var info = new FileInfo(path);
        var fallbackName = System.IO.Path.GetFileName(path);
        if (!enabled) fallbackName = fallbackName[..^DisabledSuffix.Length];

        try
        {
            using var archive = ZipFile.OpenRead(path);

            // Fabric and Quilt both ship a JSON manifest we can read directly. Forge/NeoForge
            // use TOML, which isn't worth a parser here — those fall back to the file name.
            var manifest = archive.GetEntry("fabric.mod.json") ?? archive.GetEntry("quilt.mod.json");
            if (manifest is null) return new ModEntry(path, fallbackName, Clean(fallbackName), "Unknown", info.Length, enabled, null);

            using var stream = manifest.Open();
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = document.RootElement;

            // Quilt nests everything one level down under "quilt_loader".
            if (root.TryGetProperty("quilt_loader", out var quilt)) root = quilt;

            var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            var icon = ReadIcon(archive, root);

            return new ModEntry(
                path,
                fallbackName,
                string.IsNullOrWhiteSpace(name) ? Clean(fallbackName) : name!,
                ReadAuthors(root),
                info.Length,
                enabled,
                icon);
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or IOException)
        {
            // A malformed or unreadable jar still belongs in the list — just without metadata.
            return new ModEntry(path, fallbackName, Clean(fallbackName), "Unknown", info.Length, enabled, null);
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

    /// <summary>Turns "sodium-fabric-0.5.8.jar" into something closer to a title.</summary>
    private static string Clean(string fileName) =>
        System.IO.Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ');

    /// <summary>Flips a mod on or off by renaming it, and returns its new path.</summary>
    public static string SetEnabled(ModEntry mod, bool enabled)
    {
        if (mod.Enabled == enabled) return mod.Path;

        var target = enabled
            ? mod.Path[..^DisabledSuffix.Length]
            : mod.Path + DisabledSuffix;

        File.Move(mod.Path, target, overwrite: false);
        return target;
    }
}
