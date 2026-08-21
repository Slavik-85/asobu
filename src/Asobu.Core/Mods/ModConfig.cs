using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asobu.Core.Mods;

/// <summary>What kind of control a setting wants.</summary>
public enum ConfigValueKind
{
    Boolean,
    Number,
    Text,
}

/// <summary>One setting out of a mod's config file.</summary>
public sealed class ConfigSetting
{
    /// <summary>Where it lives in the file: dotted for nesting, so two "enabled" keys stay apart.</summary>
    public required string Key { get; init; }

    /// <summary>The last part of the key, tidied into something readable.</summary>
    public required string Label { get; init; }

    /// <summary>The part before it, or null at the top level. Only used to group the form.</summary>
    public required string? Section { get; init; }

    public required ConfigValueKind Kind { get; init; }

    /// <summary>The value as text. Booleans are "true" or "false"; numbers keep their own spelling.</summary>
    public required string Value { get; set; }

    /// <summary>The comment the file had above it, where the format keeps comments.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// A mod's own settings, read from and written back to the file the mod itself uses.
///
/// This is deliberately not what ModMenu does, because what ModMenu does cannot be done from out
/// here. ModMenu is a mod: it runs inside the game and asks each other mod for a Screen, which is
/// Minecraft's own GUI code, drawn by the game's renderer. A launcher is a different program in a
/// different runtime and has no way to instantiate one, let alone draw it.
///
/// What it can do is go to where those screens end up putting things. Nearly every config library
/// in the ecosystem — Cloth, owo, Forge's own — persists to a plain file under config/, and that
/// file is the actual state the game reads at startup. Editing it here reaches the same result by
/// the same door, without the game running.
///
/// The limit of that is worth stating: this shows what is in the file, not what the mod believes
/// its options are. A setting the mod has never written has no line to find, and the meaning of
/// one that is there comes from its name and its comment rather than from the mod telling us.
/// </summary>
public sealed class ModConfig
{
    private readonly IConfigFormat _format;

    private ModConfig(string path, IConfigFormat format, IReadOnlyList<ConfigSetting> settings)
    {
        Path = path;
        _format = format;
        Settings = settings;
    }

    public string Path { get; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public IReadOnlyList<ConfigSetting> Settings { get; }

    /// <summary>
    /// False for JSON, which is rewritten from its parsed shape rather than line by line — so a
    /// file that had comments in it comes back without them. Worth telling somebody before they
    /// press save on a file they had annotated.
    /// </summary>
    public bool KeepsComments => _format.KeepsComments;

    /// <summary>The extensions worth opening. Everything else in config/ is left alone.</summary>
    private static readonly string[] Known = [".json", ".json5", ".toml", ".properties", ".cfg", ".conf"];

    /// <summary>
    /// Every config file that looks like it belongs to this mod.
    ///
    /// Matched on the mod's id, then on the file name of the jar, because a mod whose jar is
    /// called "sodium-fabric-0.5.8.jar" writes "sodium-options.json" and neither name is the
    /// other. More than one can match — Forge splits common, client and server — so this returns
    /// all of them and lets the person pick.
    /// </summary>
    public static IReadOnlyList<string> FilesFor(string instanceFolder, ModEntry mod)
    {
        var config = System.IO.Path.Combine(instanceFolder, "config");
        if (!Directory.Exists(config)) return [];

        var names = Candidates(mod);
        if (names.Count == 0) return [];

        var found = new List<string>();

        try
        {
            // Two levels: config/<something>.toml, and config/<modid>/<something>.json, which is
            // what a mod with more than a handful of options tends to do.
            foreach (var file in Directory.EnumerateFiles(config, "*", SearchOption.AllDirectories))
            {
                var extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
                if (!Known.Contains(extension)) continue;

                var stem = Normalise(System.IO.Path.GetFileNameWithoutExtension(file));
                var folder = Normalise(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file) ?? ""));

                if (names.Any(name => stem == name || stem.StartsWith(name + "-", StringComparison.Ordinal)
                                      || stem.StartsWith(name + "_", StringComparison.Ordinal))
                    || names.Contains(folder))
                {
                    found.Add(file);
                }
            }
        }
        catch (IOException)
        {
            return found;
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    /// <summary>The names this mod might have written a file under.</summary>
    private static List<string> Candidates(ModEntry mod)
    {
        var names = new List<string>();

        void Add(string? value)
        {
            if (value is not { Length: > 0 }) return;
            var clean = Normalise(value);
            if (clean.Length >= 3 && !names.Contains(clean)) names.Add(clean);
        }

        Add(mod.ModId);
        Add(StripVersion(System.IO.Path.GetFileNameWithoutExtension(mod.FileName)));
        Add(mod.Name);

        return names;
    }

    /// <summary>"sodium-fabric-0.5.8" to "sodium-fabric", so a jar name can match a config name.</summary>
    private static string StripVersion(string fileName)
    {
        var parts = fileName.Split(['-', '_', '+'], StringSplitOptions.RemoveEmptyEntries);
        var kept = parts.TakeWhile(part => !part.Any(char.IsDigit)).ToArray();
        return kept.Length > 0 ? string.Join("-", kept) : fileName;
    }

    private static string Normalise(string value) =>
        new string([.. value.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')])
            .Replace('_', '-');

    /// <summary>Reads one file, or null when it is not something this can show.</summary>
    public static ModConfig? Open(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            IConfigFormat format = System.IO.Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".json" or ".json5" => new JsonFormat(),
                _ => new LineFormat(),
            };

            var settings = format.Read(text);
            return settings.Count == 0 ? null : new ModConfig(path, format, settings);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the changed values back, and nothing else.
    ///
    /// Through a temporary file and a rename, because the thing being overwritten is the only
    /// copy of somebody's settings — a launcher killed mid-write should not be able to leave a
    /// mod with half a config file.
    /// </summary>
    public void Save(IReadOnlyDictionary<string, string> changed)
    {
        if (changed.Count == 0) return;

        var text = _format.Write(File.ReadAllText(Path), changed);

        var temp = Path + ".asobu-tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, Path, overwrite: true);
    }

    /// <summary>"enableFancyFog" to "Enable fancy fog", which is all the name gives us to work with.</summary>
    internal static string Prettify(string key)
    {
        var spaced = new StringBuilder();

        foreach (var c in key.Replace('_', ' ').Replace('-', ' '))
        {
            if (char.IsUpper(c) && spaced.Length > 0 && spaced[^1] != ' ') spaced.Append(' ');
            spaced.Append(c);
        }

        var words = spaced.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return key;

        var first = words[0];
        words[0] = char.ToUpperInvariant(first[0]) + first[1..].ToLowerInvariant();
        for (var i = 1; i < words.Length; i++) words[i] = words[i].ToLowerInvariant();

        return string.Join(' ', words);
    }

    internal static ConfigValueKind KindOf(string raw) =>
        raw is "true" or "false" ? ConfigValueKind.Boolean
        : double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? ConfigValueKind.Number
        : ConfigValueKind.Text;
}

/// <summary>One way of reading and writing a config file.</summary>
internal interface IConfigFormat
{
    bool KeepsComments { get; }
    IReadOnlyList<ConfigSetting> Read(string text);
    string Write(string original, IReadOnlyDictionary<string, string> changed);
}

/// <summary>
/// JSON, which most Fabric mods use.
///
/// Read and written through the parsed tree rather than line by line, which is why this is the
/// one format here that does not keep comments: JSON has none to keep, and the .json5 files that
/// do are being read by a parser that skips them.
/// </summary>
internal sealed class JsonFormat : IConfigFormat
{
    public bool KeepsComments => false;

    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public IReadOnlyList<ConfigSetting> Read(string text)
    {
        var settings = new List<ConfigSetting>();

        if (JsonNode.Parse(text, documentOptions: Lenient) is not JsonObject root) return settings;

        Walk(root, "", settings);
        return settings;
    }

    private static void Walk(JsonObject node, string prefix, List<ConfigSetting> into)
    {
        foreach (var (name, value) in node)
        {
            var key = prefix.Length == 0 ? name : prefix + "." + name;

            switch (value)
            {
                case JsonObject nested:
                    Walk(nested, key, into);
                    break;

                case JsonValue leaf:
                    var raw = leaf.ToJsonString().Trim('"');
                    into.Add(new ConfigSetting
                    {
                        Key = key,
                        Label = ModConfig.Prettify(name),
                        Section = prefix.Length == 0 ? null : prefix,
                        Kind = ModConfig.KindOf(raw),
                        Value = raw,
                    });
                    break;

                // Arrays are left alone. A list of strings has no sensible single control, and
                // guessing one would be a good way to corrupt somebody's keybinds.
            }
        }
    }

    public string Write(string original, IReadOnlyDictionary<string, string> changed)
    {
        if (JsonNode.Parse(original, documentOptions: Lenient) is not JsonObject root)
            throw new InvalidOperationException("that file is no longer valid JSON");

        foreach (var (key, value) in changed)
        {
            var parts = key.Split('.');
            var node = root;

            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (node[parts[i]] is JsonObject next) node = next;
                else { node = null!; break; }
            }
            if (node is null) continue;

            var leaf = parts[^1];
            node[leaf] = ModConfig.KindOf(value) switch
            {
                ConfigValueKind.Boolean => JsonValue.Create(value == "true"),
                ConfigValueKind.Number when long.TryParse(value, out var whole) => JsonValue.Create(whole),
                ConfigValueKind.Number when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
                    => JsonValue.Create(real),
                _ => JsonValue.Create(value),
            };
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}

/// <summary>
/// TOML and .properties, which Forge and a good many others use.
///
/// Handled line by line on purpose. Both formats are full of comments explaining what each option
/// does — the mod author wrote them for exactly this moment — and rewriting the file from a parsed
/// model would throw every one of them away. Only the value on a changed line is touched, so a
/// file comes back byte for byte as it went in apart from what was edited.
/// </summary>
internal sealed class LineFormat : IConfigFormat
{
    public bool KeepsComments => true;

    public IReadOnlyList<ConfigSetting> Read(string text)
    {
        var settings = new List<ConfigSetting>();
        var section = "";
        var note = new StringBuilder();

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim().TrimEnd('\r');

            if (trimmed.Length == 0) { note.Clear(); continue; }

            if (trimmed[0] is '#' or ';')
            {
                if (note.Length > 0) note.Append(' ');
                note.Append(trimmed.TrimStart('#', ';', ' '));
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed[1..^1].Trim();
                note.Clear();
                continue;
            }

            var split = trimmed.IndexOf('=');
            if (split <= 0) { note.Clear(); continue; }

            var name = trimmed[..split].Trim();
            var raw = trimmed[(split + 1)..].Trim();

            // Arrays and inline tables have no single control worth showing, same as in JSON.
            if (raw.StartsWith('[') || raw.StartsWith('{')) { note.Clear(); continue; }

            var value = Unquote(raw);

            settings.Add(new ConfigSetting
            {
                Key = section.Length == 0 ? name : section + "." + name,
                Label = ModConfig.Prettify(name),
                Section = section.Length == 0 ? null : section,
                Kind = ModConfig.KindOf(value),
                Value = value,
                Note = note.Length > 0 ? note.ToString() : null,
            });

            note.Clear();
        }

        return settings;
    }

    public string Write(string original, IReadOnlyDictionary<string, string> changed)
    {
        var lines = original.Split('\n');
        var section = "";

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim().TrimEnd('\r');

            if (trimmed.Length == 0 || trimmed[0] is '#' or ';') continue;

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed[1..^1].Trim();
                continue;
            }

            var split = trimmed.IndexOf('=');
            if (split <= 0) continue;

            var name = trimmed[..split].Trim();
            var key = section.Length == 0 ? name : section + "." + name;

            if (!changed.TryGetValue(key, out var replacement)) continue;

            var raw = trimmed[(split + 1)..].Trim();
            var quoted = raw.StartsWith('"') || raw.StartsWith('\'');

            // The indentation and the spacing around the equals are the file's own; only what is
            // to the right of it changes.
            var indent = line[..(line.Length - line.TrimStart().Length)];
            var carriage = line.EndsWith('\r') ? "\r" : "";
            var head = trimmed[..(split + 1)];

            lines[i] = indent + head + " " + (quoted ? "\"" + replacement.Replace("\"", "\\\"") + "\"" : replacement) + carriage;
        }

        return string.Join('\n', lines);
    }

    private static string Unquote(string raw)
    {
        // Anything after an unquoted value on the same line is a trailing comment, not the value.
        if (!raw.StartsWith('"') && !raw.StartsWith('\''))
        {
            var hash = raw.IndexOf('#');
            if (hash > 0) raw = raw[..hash].Trim();
            return raw;
        }

        var quote = raw[0];
        var end = raw.LastIndexOf(quote);
        return end > 0 ? raw[1..end].Replace("\\\"", "\"") : raw;
    }
}
