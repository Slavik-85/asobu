using System.Globalization;

namespace Asobu.Core.Mods;

public enum ModProvider
{
    Modrinth,
    CurseForge,
}

/// <summary>
/// One search result, flattened to the fields every provider can answer. <paramref name="GalleryUrl"/>
/// is the exception and is usually null: it is a picture from the project's own gallery, big
/// enough to fill a banner, and most mods have none.
/// </summary>
public sealed record ModListing(
    ModProvider Provider,
    string Id,
    string Title,
    string Author,
    string Summary,
    string? IconUrl,
    long Downloads,
    string PageUrl,
    string? GalleryUrl = null)
{
    /// <summary>
    /// What this is, as the provider itself reported it. Both shops say so on every search
    /// result — Modrinth in words, CurseForge as a numeric class — so this does not have to be
    /// inferred from whatever was being searched for, and an "Everything" search comes back with
    /// each result correctly labelled.
    /// </summary>
    public ModKind Kind { get; init; } = ModKind.Mod;

    public string ProviderName => Provider == ModProvider.CurseForge ? "CurseForge" : "Modrinth";

    /// <summary>
    /// Compact download count: nobody reads "209742310". Invariant on purpose — the UI is
    /// English, so a locale decimal comma in "235,5M" reads as a thousands separator and turns
    /// the number into nonsense.
    /// </summary>
    public string DownloadsLabel => Downloads switch
    {
        >= 1_000_000 => (Downloads / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M downloads",
        >= 1_000 => (Downloads / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k downloads",
        _ => $"{Downloads} downloads",
    };
}

/// <summary>
/// A jar ready to fetch. <paramref name="Url"/> is null when the provider knows of a file but is
/// not allowed to hand it over — CurseForge authors can forbid third-party downloads, and the
/// only correct response is to send the person to the project page rather than guess at a URL.
/// </summary>
public sealed record ModDownload(
    string? Url,
    string FileName,
    string? Sha1,
    long Size,
    IReadOnlyList<string>? Requires = null)
{
    /// <summary>
    /// The provider's own ids for the mods this one will not run without. Ids rather than
    /// resolved files, because each has to be looked up for this instance's version and loader
    /// like any other mod — what a dependency declares is which project it needs, not which
    /// build of it.
    /// </summary>
    public IReadOnlyList<string> Requires { get; init; } = Requires ?? [];
}

/// <summary>
/// How a listing is ordered. Both providers support all four, though neither has a true
/// "trending" — <see cref="Updated"/> is the closest honest stand-in, surfacing the mods their
/// authors are actively working on.
/// </summary>
public enum ModSort
{
    Relevance,
    Popular,
    Downloads,
    Updated,
    Newest,
}

/// <summary>
/// What is being looked for. Both providers file their catalogues by this — Modrinth as a project
/// type, CurseForge as a numeric class — and neither will give a sensible answer without it: ask
/// for "sodium" across everything and the resource packs named after it come back too.
///
/// <see cref="World"/> is CurseForge only. Modrinth has no such project type, and pretending
/// otherwise would mean an empty list with no explanation.
/// </summary>
public enum ModKind
{
    Any,
    Mod,
    Modpack,
    ResourcePack,
    Shader,
    DataPack,
    World,
}

/// <summary>
/// Where each kind of content is loaded from, and how the two shops name it.
///
/// Getting this wrong is quiet rather than loud: a shader pack in `mods/` is not an error, it is
/// a file the game never looks at, and the person is left wondering why their shaders never
/// appeared in the menu.
/// </summary>
public static class ModContent
{
    /// <summary>
    /// The instance subfolder, or null for something that cannot simply be dropped in — a world
    /// has to be unpacked into a save, and a modpack is an instance rather than a file in one.
    /// </summary>
    public static string? FolderFor(ModKind kind) => kind switch
    {
        ModKind.ResourcePack => "resourcepacks",
        ModKind.Shader => "shaderpacks",

        // Vanilla reads data packs from inside a world. An instance-level folder is where every
        // other launcher stages them, and it is at least somewhere the person can find them.
        ModKind.DataPack => "datapacks",

        // Downloaded worlds are unpacked into saves/ beside the player's own, and marked so
        // the launcher can tell the two apart — see ModScanner.WorldMarker.
        ModKind.World => "saves",

        ModKind.Modpack => null,

        // Mods, and anything whose kind never came back. Mods are the overwhelming majority and
        // the folder a stray file does least harm in.
        _ => "mods",
    };

    /// <summary>
    /// Where this kind of content lives inside an instance. Not the same question as
    /// <see cref="FolderFor"/>, which is where a *download* may be put: a world has a folder and
    /// can be listed and deleted, but cannot be installed as a file, because unpacking one into
    /// a save is an import rather than a download.
    /// </summary>
    public static string? LocalFolderFor(ModKind kind) => kind switch
    {
        ModKind.Modpack => null,
        _ => FolderFor(kind),
    };

    /// <summary>
    /// Whether a loader is part of the question at all. A resource pack is not built against
    /// Fabric, and asking either provider for one that is returns nothing — which reads as "no
    /// build for your instance" when the truth is that it runs on every instance.
    /// </summary>
    public static bool NeedsLoader(ModKind kind) => kind is ModKind.Mod or ModKind.Modpack or ModKind.Any;

    /// <summary>
    /// Every kind that lives in a folder of its own, which is what "Everything" is a list of.
    /// </summary>
    public static readonly IReadOnlyList<ModKind> Local =
        [ModKind.Mod, ModKind.ResourcePack, ModKind.Shader, ModKind.DataPack, ModKind.World];

    /// <summary>CurseForge files its catalogue by numeric class.</summary>
    public static ModKind KindForClass(int classId) => classId switch
    {
        6 => ModKind.Mod,
        12 => ModKind.ResourcePack,
        17 => ModKind.World,
        4471 => ModKind.Modpack,
        6552 => ModKind.Shader,
        6945 => ModKind.DataPack,
        _ => ModKind.Any,
    };

    /// <summary>Modrinth says it in words, on both search hits and projects.</summary>
    public static ModKind KindForProjectType(string? projectType) => projectType?.ToLowerInvariant() switch
    {
        "mod" => ModKind.Mod,
        "modpack" => ModKind.Modpack,
        "resourcepack" => ModKind.ResourcePack,
        "shader" => ModKind.Shader,
        "datapack" => ModKind.DataPack,
        _ => ModKind.Any,
    };
}

public sealed record ModQuery(
    string Text,
    string? GameVersion,
    string? Loader,
    ModSort Sort = ModSort.Relevance,
    IReadOnlyList<string>? Categories = null,
    int Limit = 40,
    int Offset = 0,
    ModKind Kind = ModKind.Mod)
{
    /// <summary>Whether a loader is worth filtering on. See <see cref="ModContent.NeedsLoader"/>.</summary>
    public bool LoaderApplies => Kind is ModKind.Mod or ModKind.Modpack;
}

/// <summary>
/// A place mods come from. The interface exists so the browser never learns the shape of any one
/// provider's API, which is what makes CurseForge optional rather than load-bearing.
/// </summary>
public interface IModSource
{
    ModProvider Provider { get; }

    /// <summary>False when the source cannot be used yet, such as a missing API key.</summary>
    bool IsAvailable { get; }

    Task<IReadOnlyList<ModListing>> SearchAsync(ModQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// What this source can filter by for a given kind, as the browser should label them. Asked
    /// for rather than hardcoded: both providers add categories, and a table written out here
    /// would start going stale the day it was written.
    /// </summary>
    Task<IReadOnlyList<string>> GetCategoriesAsync(ModKind kind, CancellationToken cancellationToken = default);

    /// <summary>
    /// The build to install for this instance. <paramref name="kind"/> decides whether the
    /// loader is part of the question — see <see cref="ModContent.NeedsLoader"/>.
    /// </summary>
    Task<ModDownload?> GetDownloadAsync(
        string modId, string gameVersion, string loader,
        ModKind kind = ModKind.Mod, CancellationToken cancellationToken = default);

    /// <summary>The long description and gallery, for a mod's own page.</summary>
    Task<ModDetails?> GetDetailsAsync(string modId, CancellationToken cancellationToken = default);

    /// <summary>Every build the author has published, newest first.</summary>
    Task<IReadOnlyList<ModVersion>> GetVersionsAsync(
        string modId, CancellationToken cancellationToken = default);
}
