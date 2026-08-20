namespace Asobu.Core.Mods;

/// <summary>
/// One idea, under whatever name each provider files it. Modrinth uses lowercase slugs and keeps
/// its lists short; CurseForge writes categories out and slices them finer. Neither publishes a
/// mapping to the other, so ticking "Equipment" would otherwise search Modrinth and leave
/// CurseForge out of it — or the reverse.
///
/// A provider can name several: CurseForge splits what Modrinth calls <c>technology</c> across
/// Automation, Energy, Processing and Redstone, and all of them belong under the one tick.
///
/// Scoped to a kind because the same word means different things in different parts of a
/// catalogue: <c>adventure</c> pairs with "Adventure and RPG" among mods, and with that plus
/// "Exploration" among modpacks.
/// </summary>
public sealed record CategoryConcept(ModKind Kind, string Label, string[] Modrinth, string[] CurseForge);

/// <summary>
/// Joins the two taxonomies together.
///
/// Names that already agree need no entry here — <see cref="Key"/> strips case and punctuation,
/// so Food, Magic, Mobs, Storage, Quests, Fantasy, 16x and the rest pair themselves, as do the
/// ones differing only in how they are written ("Armor, Tools, and Weapons" against "Armor Tools
/// and Weapons"). Written out below is only the genuinely different wording, which is what keeps
/// this a short table rather than a copy of both catalogues.
///
/// A name neither the lists nor this table pair still works: it filters the provider that knows
/// it and excludes the one that does not, which is the honest answer when only one shop sorts
/// things that way at all. The same is true of a name here spelt wrongly — it resolves to no id
/// and that provider bows out, rather than answering the search unfiltered.
/// </summary>
public static class CategoryMap
{
    private static readonly CategoryConcept[] Concepts =
    [
        // ---- Mods ----
        new(ModKind.Mod, "Adventure", ["adventure"], ["Adventure and RPG"]),
        new(ModKind.Mod, "Equipment", ["equipment"], ["Armor, Tools, and Weapons"]),
        new(ModKind.Mod, "Technology", ["technology"],
            ["Technology", "Automation", "Energy", "Processing", "Redstone"]),
        new(ModKind.Mod, "Transportation", ["transportation"],
            ["Player Transport", "Energy, Fluid, and Item Transport"]),
        new(ModKind.Mod, "World generation", ["worldgen"],
            ["World Gen", "Biomes", "Dimensions", "Structures", "Ores and Resources"]),
        new(ModKind.Mod, "Utility", ["utility"],
            ["Utility & QoL", "Server Utility", "Map and Information", "Miscellaneous"]),
        new(ModKind.Mod, "Library", ["library"], ["API and Library"]),
        new(ModKind.Mod, "Optimization", ["optimization"], ["Performance"]),
        new(ModKind.Mod, "Decoration", ["decoration"], ["Cosmetic"]),
        new(ModKind.Mod, "Management", ["management"], ["Farming", "Genetics"]),
        new(ModKind.Mod, "Social", ["social"], ["Twitch Integration"]),

        // ---- Modpacks ----
        new(ModKind.Modpack, "Adventure", ["adventure"], ["Adventure and RPG", "Exploration"]),
        new(ModKind.Modpack, "Combat", ["combat"], ["Combat / PvP"]),
        new(ModKind.Modpack, "Technology", ["technology"], ["Tech"]),
        new(ModKind.Modpack, "Lightweight", ["lightweight"], ["Small / Light"]),
        new(ModKind.Modpack, "Kitchen sink", ["kitchen-sink"], ["Extra Large"]),
        new(ModKind.Modpack, "Challenging", ["challenging"], ["Hardcore"]),

        // ---- Resource packs ----
        new(ModKind.ResourcePack, "Realistic", ["realistic"], ["Photo Realistic"]),
        new(ModKind.ResourcePack, "Vanilla-like", ["vanilla-like"], ["Traditional"]),
        new(ModKind.ResourcePack, "Modded", ["modded"], ["Mod Support"]),
        new(ModKind.ResourcePack, "Fonts", ["fonts"], ["Font Packs"]),
        new(ModKind.ResourcePack, "Themed", ["themed"], ["Medieval", "Modern", "Steampunk"]),
        new(ModKind.ResourcePack, "512x+", ["512x+"], ["512x and Higher"]),

        // ---- Shaders ----
        new(ModKind.Shader, "Vanilla-like", ["vanilla-like"], ["Vanilla"]),
    ];

    private static readonly Dictionary<ModKind, Dictionary<string, CategoryConcept>> ByKind = Build();

    /// <summary>
    /// The label a provider's own name belongs under, or the name itself when nothing pairs with
    /// it. Case and punctuation are ignored, so a name written slightly differently still lands.
    /// </summary>
    public static string LabelFor(ModKind kind, string providerName) =>
        Find(kind, providerName)?.Label ?? providerName;

    /// <summary>What Modrinth calls this label, or the label itself if nothing pairs with it.</summary>
    public static IReadOnlyList<string> ModrinthNames(ModKind kind, string label) =>
        Find(kind, label)?.Modrinth ?? [label];

    /// <summary>What CurseForge calls this label, or the label itself if nothing pairs with it.</summary>
    public static IReadOnlyList<string> CurseForgeNames(ModKind kind, string label) =>
        Find(kind, label)?.CurseForge ?? [label];

    private static CategoryConcept? Find(ModKind kind, string name) =>
        ByKind.TryGetValue(kind, out var concepts) && concepts.TryGetValue(Key(name), out var concept)
            ? concept
            : null;

    /// <summary>
    /// Letters and digits only, lowercased. "Armor, Tools, and Weapons", "Armor Tools and
    /// Weapons" and "armor tools and weapons" are one category written three ways.
    /// </summary>
    private static string Key(string name) =>
        new([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    private static Dictionary<ModKind, Dictionary<string, CategoryConcept>> Build()
    {
        var byKind = new Dictionary<ModKind, Dictionary<string, CategoryConcept>>();

        foreach (var concept in Concepts)
        {
            if (!byKind.TryGetValue(concept.Kind, out var concepts))
                byKind[concept.Kind] = concepts = new Dictionary<string, CategoryConcept>(StringComparer.Ordinal);

            // The label first, so looking a label up finds its own concept and not a sibling's.
            concepts.TryAdd(Key(concept.Label), concept);

            foreach (var name in concept.Modrinth) concepts.TryAdd(Key(name), concept);
            foreach (var name in concept.CurseForge) concepts.TryAdd(Key(name), concept);
        }

        return byKind;
    }
}
