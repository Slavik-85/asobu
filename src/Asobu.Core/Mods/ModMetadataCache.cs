using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asobu.Core.Mods;

/// <summary>
/// Remembers what was inside each jar, so it only ever has to be opened once.
///
/// Reading a mods folder means opening every jar as a zip, finding its manifest, parsing JSON and
/// pulling out an icon. For a folder of twenty-five that is over a second on a cold disk, and it
/// happens every time an instance is opened, rescanned or switched to. Nothing in a jar changes
/// while it sits there, so the answer is worth keeping.
///
/// Keyed on path, write time and length together. A path alone would hand back the old answer
/// after a mod is updated in place; all three together change whenever the file does.
///
/// Kept on disk as well as in memory, because the first read after launching Asobu is exactly the
/// one people notice.
/// </summary>
public sealed class ModMetadataCache
{
    private readonly string _file;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Set when something new was learned, so an unchanged folder writes nothing.</summary>
    private int _dirty;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ModMetadataCache(AsobuPaths paths)
    {
        _file = Path.Combine(paths.Cache, "mod-metadata.json");

        try
        {
            if (!File.Exists(_file)) return;

            var stored = JsonSerializer.Deserialize<Dictionary<string, Entry>>(
                File.ReadAllText(_file), Options);

            foreach (var (key, entry) in stored ?? []) _entries[key] = entry;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged cache is a slow scan, not a broken launcher.
        }
    }

    /// <summary>What a file is keyed by: where it is, when it changed, and how big it is.</summary>
    public static string KeyFor(FileInfo file) =>
        $"{file.FullName}|{file.LastWriteTimeUtc.Ticks}|{file.Length}";

    public bool TryGet(string key, out ModEntry entry)
    {
        if (_entries.TryGetValue(key, out var stored))
        {
            entry = stored.ToMod();
            return true;
        }

        entry = null!;
        return false;
    }

    public void Put(string key, ModEntry entry)
    {
        _entries[key] = Entry.From(entry);
        Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>
    /// Writes the cache back, if anything was learned. Called after a scan rather than on every
    /// entry: a folder of forty mods should cost one write, not forty.
    /// </summary>
    public void Save()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);

            // Through a temporary file: a half-written cache that fails to parse next time would
            // undo the very thing this is for.
            var temporary = _file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_entries, Options));
            File.Move(temporary, _file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not being able to write it costs a slow scan next time and nothing else.
        }
    }

    /// <summary>
    /// The stored shape. Deliberately not <see cref="ModEntry"/> itself: that record carries the
    /// path and enabled flag, which belong to the file on disk rather than to what is inside it,
    /// and storing them would mean a rename invalidated a perfectly good answer.
    /// </summary>
    private sealed record Entry(string Name, string Author, string? ModId, byte[]? IconPng)
    {
        public static Entry From(ModEntry mod) => new(mod.Name, mod.Author, mod.ModId, mod.IconPng);

        /// <summary>
        /// Filled in with the file's own facts by the caller — this half only knows the contents.
        /// </summary>
        public ModEntry ToMod() => new("", "", Name, Author, ModId, 0, true, IconPng);
    }
}
