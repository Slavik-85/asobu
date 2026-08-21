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
        foreach (var entry in entries)
            foreach (var name in NamesOf(entry))
                _byName.TryAdd(name, entry);
    }

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
