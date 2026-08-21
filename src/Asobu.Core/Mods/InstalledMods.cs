using Asobu.Core.Instances;

namespace Asobu.Core.Mods;

/// <summary>
/// What an instance already has, indexed so a search result can be asked whether it is one of
/// them.
///
/// Matched on names rather than on a record of what Asobu downloaded, because the copies that
/// matter most are the ones it did not put there — dropped into the folder by hand, carried in by
/// a shared instance, or installed by whatever launcher was in use before this one. A launcher
/// that only recognised its own downloads would offer to add a second copy of every one of them,
/// which is the whole complaint.
///
/// The names that do the work are the mod id a jar declares and the slug at the end of a project
/// page — "cloth_config" against "cloth-config", once the punctuation is taken out. The display
/// title is tried too, and matches less often than either: shops write "Just Enough Items (JEI)"
/// where the jar simply says "jei".
/// </summary>
public sealed class InstalledMods
{
    /// <summary>Every kind that lives in a folder of its own. A modpack is an instance, not a file in one.</summary>
    private static readonly ModKind[] Kinds =
        [ModKind.Mod, ModKind.ResourcePack, ModKind.Shader, ModKind.DataPack, ModKind.World];

    private readonly Dictionary<string, ModEntry> _byName = new(StringComparer.Ordinal);

    private InstalledMods(IEnumerable<ModEntry> entries)
    {
        var all = entries.ToList();

        // Two passes, and the order is the point. What a mod calls itself is registered for every
        // mod first; only then does anything derived from a file name get to claim a key. A jar
        // named after one project must never answer for another that actually declares that id.
        foreach (var entry in all)
            foreach (var name in NamesOf(entry))
                _byName.TryAdd(name, entry);

        foreach (var entry in all)
            if (FileNameKey(entry) is { } key)
                _byName.TryAdd(key, entry);
    }

    /// <summary>
    /// The jar's own file name, with the loader it was built for taken off the end.
    ///
    /// The third name a mod goes by, and the one that saves the cases where the other two both
    /// miss. Advanced XRay declares its id as "xray" and titles itself "Advanced XRay (Fabric)",
    /// while its page is at advanced-xray — so the id is too short, the title has a loader
    /// bracketed onto it, and nothing matches. The file is called advanced-xray-fabric-26.2.0.1,
    /// which is the page's own name with a platform and a version after it.
    ///
    /// Only the trailing platform words go. Taking them from anywhere would turn fabric-api into
    /// api, which is a different thing entirely and probably somebody else's.
    /// </summary>
    private static string? FileNameKey(ModEntry entry)
    {
        if (ProjectStem(entry.FileName) is not { } stem) return null;

        var pieces = stem.Split('-').ToList();
        while (pieces.Count > 1 && Platforms.Contains(pieces[^1]))
            pieces.RemoveAt(pieces.Count - 1);

        return Clean(string.Join('-', pieces));
    }

    /// <summary>What a jar's name says about where it runs rather than about what it is.</summary>
    private static readonly HashSet<string> Platforms = new(StringComparer.OrdinalIgnoreCase)
    {
        "fabric", "forge", "neoforge", "quilt", "mc", "client", "server", "mod", "universal",
    };

    public static readonly InstalledMods Empty = new([]);

    /// <summary>
    /// Everything installed in an instance. One scan of each content folder, which the metadata
    /// cache makes cheap after the first — but still worth keeping off the UI thread on a cold
    /// disk, since a folder of forty jars is forty files to stat.
    /// </summary>
    public static InstalledMods For(AsobuPaths paths, Instance instance)
    {
        var entries = new List<ModEntry>();

        foreach (var kind in Kinds)
        {
            if (ModScanner.ContentDirectory(paths, instance.Folder, kind) is not { } directory) continue;

            entries.AddRange(ModScanner.Scan(directory, kind));
        }

        return new InstalledMods(entries);
    }

    /// <summary>The file this mod is already installed as, or null when it is not.</summary>
    public ModEntry? Find(CatalogueMod mod)
    {
        foreach (var name in NamesOf(mod))
            if (_byName.TryGetValue(name, out var entry)) return entry;

        return null;
    }

    public bool Has(CatalogueMod mod) => Find(mod) is not null;

    /// <summary>
    /// The installed file that looks like an older build of <paramref name="fileName"/>, or null
    /// when there is no such file or no telling which it is.
    ///
    /// For dependencies, which arrive as a provider id and a file name and nothing else — there
    /// is no project name to match on the way there is for a search result, so the file name is
    /// all there is to go on.
    ///
    /// Deliberately gives up when two files could be it. A duplicate is a mess the loader will
    /// complain about; deleting the wrong mod is somebody's afternoon.
    /// </summary>
    public static ModEntry? OlderBuildOf(string fileName, IEnumerable<ModEntry> installed)
    {
        if (ProjectStem(fileName) is not { } stem) return null;

        ModEntry? found = null;

        foreach (var entry in installed)
        {
            // The same build, already there. Nothing to replace, and saying otherwise would
            // have the caller delete what it just downloaded.
            if (string.Equals(entry.FileName, fileName, StringComparison.OrdinalIgnoreCase)) return null;

            if (!string.Equals(ProjectStem(entry.FileName), stem, StringComparison.OrdinalIgnoreCase))
                continue;

            // A second candidate means the stem is not telling them apart. Leave both alone.
            if (found is not null) return null;

            found = entry;
        }

        return found;
    }

    /// <summary>
    /// The part of a file name that names the project rather than the build: everything up to
    /// the first piece that begins with a digit.
    ///
    ///     fabric-api-0.115.0+1.21.1.jar  →  fabric-api
    ///     sodium-fabric-0.9.1.jar        →  sodium-fabric
    ///
    /// Crude, and only used where nothing better exists. It errs towards keeping too much of the
    /// name, which costs a duplicate rather than a wrongly deleted mod: "sodium" and
    /// "sodium-extra" stay two projects, as they should.
    /// </summary>
    public static string? ProjectStem(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);

        // "+1.21.1" is build metadata rather than part of the name, and splitting on it first
        // keeps a stem from swallowing the game version behind it.
        if (name.IndexOf('+') is >= 0 and var plus) name = name[..plus];

        var pieces = name.Split('-', '_');
        var kept = pieces.TakeWhile(piece => piece.Length > 0 && !char.IsDigit(piece[0])).ToList();

        if (kept.Count == 0) return null;

        var stem = string.Join('-', kept);

        return stem.Length >= 3 ? stem.ToLowerInvariant() : null;
    }

    /// <summary>
    /// What a catalogue entry might be known by: the slug from each shop's page, then the title.
    ///
    /// The slug leads because it is the closest thing either shop has to the id a jar declares —
    /// modrinth.com/mod/sodium against a jar saying "sodium" — and the title is the loosest,
    /// which is why it is tried last.
    /// </summary>
    private static IEnumerable<string> NamesOf(CatalogueMod mod)
    {
        foreach (var listing in new[] { mod.Modrinth, mod.CurseForge })
        {
            if (listing is null) continue;

            if (Clean(SlugOf(listing.PageUrl)) is { } slug) yield return slug;
        }

        if (Clean(mod.Title) is { } title) yield return title;
    }

    private static IEnumerable<string> NamesOf(ModEntry entry)
    {
        if (Clean(entry.ModId) is { } id) yield return id;
        if (Clean(entry.Name) is { } name) yield return name;
    }

    /// <summary>
    /// The last segment of a project page's address, which both shops end with the slug:
    /// modrinth.com/mod/<b>sodium</b>, curseforge.com/minecraft/mc-mods/<b>jei</b>.
    /// </summary>
    private static string? SlugOf(string? pageUrl)
    {
        if (pageUrl is not { Length: > 0 }) return null;

        // A query or a fragment is not part of the slug, and a trailing slash would make the
        // last segment the empty one.
        var text = pageUrl.Split('?', '#')[0].TrimEnd('/');
        var cut = text.LastIndexOf('/');

        return cut >= 0 && cut < text.Length - 1 ? text[(cut + 1)..] : null;
    }

    /// <summary>
    /// A name reduced to the part that is actually the name: lower case, letters and digits only.
    /// That is what lets "cloth-config", "cloth_config" and "Cloth Config" be recognised as one
    /// thing.
    ///
    /// Null for anything too short to be meant, which keeps a one-letter file name from matching
    /// half the catalogue.
    /// </summary>
    private static string? Clean(string? name)
    {
        if (name is not { Length: > 0 }) return null;

        var kept = new char[name.Length];
        var length = 0;

        foreach (var character in name)
            if (char.IsLetterOrDigit(character))
                kept[length++] = char.ToLowerInvariant(character);

        return length >= 2 ? new string(kept, 0, length) : null;
    }
}
