using System.Text.Json;
using Asobu.Core.Instances;

namespace Asobu.Core.Mods;

/// <summary>What a shop said a mod was called, at the moment it was downloaded.</summary>
/// <param name="Name">The project's own title — "Just Enough Items", not "jei-1.21-19.0.0".</param>
/// <param name="Author">Whoever the shop credits.</param>
/// <param name="Provider">Which shop, so the row can say where it came from.</param>
public sealed record ModCredit(string Name, string Author, string Provider);

/// <summary>
/// Names for the mods whose own jars do not carry one.
///
/// Most declare themselves properly and none of this is needed. A few do not: Essential's Fabric
/// jar holds an id and a version and no name at all, and its Forge jar has no manifest whatsoever,
/// so the only name the launcher can find is the file's. That reads as a broken row sitting beside
/// properly named ones.
///
/// But the launcher did know. It downloaded that file from a catalogue that had the project's real
/// title, its author and where it came from, and then threw all of it away. So it is written down
/// beside the instance instead — one small file, next to instance.json rather than inside the game
/// folder, where nothing but Asobu will ever look at it.
///
/// Only ever a better name for a file that is already there. Losing this file loses nothing but
/// the prettier spelling, which is why nothing here fails loudly.
/// </summary>
public sealed class ModCredits
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<string, ModCredit> _byFile;

    private ModCredits(Dictionary<string, ModCredit> byFile) => _byFile = byFile;

    public static readonly ModCredits Empty = new(new(StringComparer.OrdinalIgnoreCase));

    private static string FileFor(AsobuPaths paths, Instance instance) =>
        Path.Combine(paths.InstanceDir(instance.Folder), "asobu-mods.json");

    public static ModCredits For(AsobuPaths paths, Instance instance)
    {
        try
        {
            var file = FileFor(paths, instance);
            if (!File.Exists(file)) return Empty;

            var stored = JsonSerializer.Deserialize<Dictionary<string, ModCredit>>(
                File.ReadAllText(file), Options);

            return stored is null
                ? Empty
                : new ModCredits(new Dictionary<string, ModCredit>(stored, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return Empty;
        }
    }

    /// <summary>What the shop called this file, or null when it was not downloaded from one.</summary>
    public ModCredit? Get(string fileName) =>
        _byFile.TryGetValue(fileName, out var credit) ? credit : null;

    /// <summary>
    /// A better name over a scanned file, where there is one worth having.
    ///
    /// Only fills in what the jar left out. A mod that names itself keeps its own name — the shop
    /// and the author sometimes disagree about capitals, and the author is the one who wrote it.
    /// </summary>
    public ModEntry Dress(ModEntry entry)
    {
        if (Get(entry.FileName) is not { } credit) return entry;

        return entry with
        {
            Name = entry.Declared ? entry.Name : credit.Name,
            Author = entry.Author is "Unknown" or "" ? credit.Author : entry.Author,
        };
    }

    /// <summary>Writes down what a shop called a file it just handed over.</summary>
    public static void Record(
        AsobuPaths paths, Instance instance, string fileName, ModCredit credit)
    {
        try
        {
            var file = FileFor(paths, instance);

            // A copy, never the loaded dictionary itself: an instance with no file yet comes back
            // as the shared Empty, and writing into that would hand the next instance somebody
            // else's names.
            var all = new Dictionary<string, ModCredit>(
                For(paths, instance)._byFile, StringComparer.OrdinalIgnoreCase)
            {
                [fileName] = credit,
            };

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            // Through a temporary file, so a launcher closed mid-write leaves the old names rather
            // than a file that will not parse.
            var temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, Options));
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A name is a nicety. Failing an install over one would not be.
        }
    }
}
