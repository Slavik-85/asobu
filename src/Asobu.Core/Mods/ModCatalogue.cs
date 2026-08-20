namespace Asobu.Core.Mods;

/// <summary>
/// One mod, as both providers know it. The two catalogues carry most of the same mods under
/// slightly different names, and somebody searching for Sodium wants Sodium — not a choice
/// between two shops that both stock it.
///
/// Holding both listings is also what makes the download fall back: CurseForge authors can
/// forbid third-party downloads, and when they do, the same mod on Modrinth usually can be had.
/// </summary>
public sealed record CatalogueMod(ModListing? Modrinth, ModListing? CurseForge)
{
    /// <summary>
    /// What this actually is, taken from the search that found it. It changes what Add can
    /// possibly mean — a mod goes into an instance, a modpack becomes one — so it travels with
    /// the entry rather than being inferred later from whatever the page happens to know.
    ///
    /// <see cref="ModKind.Any"/> means the search did not narrow it down and neither can we.
    /// </summary>
    public ModKind Kind { get; init; } =
        (Modrinth ?? CurseForge)?.Kind is { } reported and not ModKind.Any ? reported : ModKind.Mod;

    /// <summary>A whole pack, which is an instance rather than something to put in one.</summary>
    public bool IsPack => Kind == ModKind.Modpack;

    /// <summary>
    /// Where the browser reads its text and pictures from. Modrinth where there is a choice: its
    /// summaries are written for humans and its gallery gives out full-size images.
    /// </summary>
    public ModListing Display => Modrinth ?? CurseForge
        ?? throw new InvalidOperationException("A mod entry needs at least one listing.");

    public string Title => Display.Title;
    public string Summary => Display.Summary;
    public string Author => Display.Author;
    public string? IconUrl => Display.IconUrl;
    public string PageUrl => Display.PageUrl;
    public string DownloadsLabel => Display.DownloadsLabel;

    /// <summary>The larger of the two counts. Neither provider sees the other's downloads.</summary>
    public long Downloads => Math.Max(Modrinth?.Downloads ?? 0, CurseForge?.Downloads ?? 0);

    /// <summary>Either provider's gallery will do; Modrinth's is the better picture.</summary>
    public string? GalleryUrl => Modrinth?.GalleryUrl ?? CurseForge?.GalleryUrl;

    /// <summary>Named honestly: on a merged page it is worth knowing who has a given mod.</summary>
    public string SourceLabel => Modrinth is not null && CurseForge is not null
        ? "Both"
        : Display.ProviderName;

    /// <summary>
    /// CurseForge first, then Modrinth. The order is deliberate: CurseForge is where a mod's
    /// author is most likely to be publishing officially, and Modrinth is the one that will
    /// still hand over a file when the other refuses.
    /// </summary>
    public IEnumerable<ModListing> DownloadOrder
    {
        get
        {
            if (CurseForge is not null) yield return CurseForge;
            if (Modrinth is not null) yield return Modrinth;
        }
    }

    /// <summary>A key both providers agree on, given neither publishes the other's ids.</summary>
    public static string KeyFor(string title) =>
        new([.. title.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}

/// <summary>
/// Both mod providers behind one search. The browser asks this rather than picking a shop,
/// because which shop a mod happens to be listed in is the launcher's problem, not the player's.
/// </summary>
public sealed class ModCatalogue(Modrinth modrinth, CurseForge curseForge)
{
    /// <summary>
    /// How many Modrinth results go in for each CurseForge one. Both lists arrive ranked, but
    /// CurseForge has no relevance sort at all — the ranking it returns is popularity with a
    /// name-match nudge — so leading with Modrinth puts the obvious answer first while still
    /// giving the mods only CurseForge carries a real place on the page.
    /// </summary>
    private const int ModrinthShare = 2;

    /// <summary>The finished, ranked list per kind. Working it out costs four requests.</summary>
    private readonly Dictionary<ModKind, IReadOnlyList<string>> _ordered = [];

    /// <summary>
    /// Answers already given, so going back to a page shows what it showed before instead of
    /// asking two web services the same question again. Around half a second is saved every
    /// time, which is the difference between a page appearing and a page loading.
    ///
    /// Short-lived on purpose: a catalogue does change, and a launcher that showed yesterday's
    /// results because it once asked would be worse than one that waits.
    /// </summary>
    private static readonly TimeSpan SearchMemory = TimeSpan.FromMinutes(5);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<CatalogueMod> Results)> _searches = new(StringComparer.Ordinal);

    public Modrinth Modrinth { get; } = modrinth;
    public CurseForge CurseForge { get; } = curseForge;

    /// <summary>True when CurseForge is out of the picture, so the browser can say why.</summary>
    public bool CurseForgeMissing => !CurseForge.IsAvailable;
    public bool CurseForgeRejected => CurseForge.IsAvailable && CurseForge.KeyRejected;

    /// <summary>
    /// Everything either provider will filter by for this kind, as one list of ticks. Both
    /// catalogues' names go through <see cref="CategoryMap"/> first, so the ones that mean the
    /// same thing land on the same label and appear once — tick Equipment and it narrows
    /// Modrinth's <c>equipment</c> and CurseForge's "Armor, Tools, and Weapons" together.
    ///
    /// What neither list pairs keeps its own name and filters the one provider that files under
    /// it. That is the honest answer: only one shop sorts things that way at all.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetCategoriesAsync(
        ModKind kind, CancellationToken cancellationToken = default)
    {
        if (_ordered.TryGetValue(kind, out var cached)) return cached;

        var fromModrinth = Modrinth.GetCategoriesAsync(kind, cancellationToken);
        var fromCurseForge = CurseForge.GetCategoriesAsync(kind, cancellationToken);

        await Task.WhenAll(fromModrinth, fromCurseForge).ConfigureAwait(false);

        // Weights only after the names, never alongside them. Working out a weight needs the name
        // list to sift loader tags out of it, so running the two together had each provider
        // fetching and writing its own cache from two threads at once — which threw often enough
        // to leave the filter panel empty.
        var modrinthWeights = Modrinth.GetCategoryWeightsAsync(kind, cancellationToken);
        var curseForgeWeights = CurseForge.GetCategoryWeightsAsync(kind, cancellationToken);

        await Task.WhenAll(modrinthWeights, curseForgeWeights).ConfigureAwait(false);

        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var weights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // CurseForge first: where nothing pairs a name, its own wording is the more readable of
        // the two — "Map and Information" against "utility".
        foreach (var name in await fromCurseForge) Add(name);
        foreach (var name in await fromModrinth) Add(name);

        // Both providers' counts land on the same label, which is the point of the map: what
        // CurseForge files under five names and Modrinth under one is one line in the panel, and
        // should be ranked by everything either of them put there.
        foreach (var (name, count) in await modrinthWeights) Weigh(name, count);
        foreach (var (name, count) in await curseForgeWeights) Weigh(name, count);

        IReadOnlyList<string> ordered =
        [
            .. labels.Values
                .OrderByDescending(label => weights.GetValueOrDefault(label))
                .ThenBy(label => label, StringComparer.OrdinalIgnoreCase),
        ];

        // Only kept once something arrived: an outage should not cache an empty panel for the
        // lifetime of the application.
        if (ordered.Count > 0) _ordered[kind] = ordered;

        return ordered;

        void Add(string name)
        {
            var label = CategoryMap.LabelFor(kind, name);
            labels.TryAdd(label, Title(label));
        }

        void Weigh(string name, int count)
        {
            var label = Title(CategoryMap.LabelFor(kind, name));
            weights[label] = weights.GetValueOrDefault(label) + count;
        }
    }

    /// <summary>
    /// A shorter list for a row of shortcut chips: Modrinth's own, which are a tidy two dozen
    /// rather than the union's hundred-odd, and which read as single words. Put through the map
    /// like everything else, so a chip narrows both shops wherever the two have a name for the
    /// same idea.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetShortcutCategoriesAsync(
        ModKind kind, CancellationToken cancellationToken = default)
    {
        var mine = (await Modrinth.GetCategoriesAsync(kind, cancellationToken).ConfigureAwait(false))
            .Select(name => Title(CategoryMap.LabelFor(kind, name)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Filtered out of the ranked list rather than ranked separately, so the row reads in the
        // same order as the panel it is a shortcut to.
        return [.. (await GetCategoriesAsync(kind, cancellationToken).ConfigureAwait(false))
            .Where(mine.Contains)];
    }

    /// <summary>"worldgen" reads as a slug; "Worldgen" reads as a label.</summary>
    private static string Title(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    public async Task<IReadOnlyList<CatalogueMod>> SearchAsync(
        ModQuery query, CancellationToken cancellationToken = default)
    {
        var key = KeyOf(query);

        if (_searches.TryGetValue(key, out var remembered)
            && DateTimeOffset.UtcNow - remembered.At < SearchMemory)
            return remembered.Results;

        var fromModrinth = Modrinth.SearchAsync(query, cancellationToken);

        // Asked for in parallel: one provider being slow should not double how long a search takes.
        var fromCurseForge = CurseForge.IsAvailable
            ? CurseForge.SearchAsync(query, cancellationToken)
            : Task.FromResult<IReadOnlyList<ModListing>>([]);

        await Task.WhenAll(fromModrinth, fromCurseForge).ConfigureAwait(false);

        // The listings carry their own kind, which is the better answer — an "Everything"
        // search comes back with each result labelled as what it actually is. The query only
        // fills in where a provider said nothing.
        var merged = (IReadOnlyList<CatalogueMod>)[.. Merge(await fromModrinth, await fromCurseForge, query.Text)
            .Select(mod => mod.Kind == ModKind.Any && query.Kind != ModKind.Any
                ? mod with { Kind = query.Kind }
                : mod)];

        // An empty page is not worth remembering: it is usually a provider that was unreachable
        // for a moment, and holding onto it would keep the page empty for five minutes.
        if (merged.Count > 0) _searches[key] = (DateTimeOffset.UtcNow, merged);

        return merged;
    }

    /// <summary>Everything about a query that changes its answer.</summary>
    private static string KeyOf(ModQuery query) => string.Join(
        '',
        query.Text,
        query.GameVersion,
        query.Loader,
        query.Sort,
        query.Kind,
        query.Limit,
        query.Offset,
        query.Categories is { Count: > 0 } categories ? string.Join(',', categories.OrderBy(c => c, StringComparer.Ordinal)) : "");

    /// <summary>
    /// The long description and gallery, from whichever provider the page is showing. Not merged:
    /// two descriptions of the same mod is one more than anybody needs, and the one being read is
    /// the one the rest of the page came from.
    /// </summary>
    public Task<ModDetails?> GetDetailsAsync(
        CatalogueMod mod, CancellationToken cancellationToken = default) =>
        SourceFor(mod.Display.Provider).GetDetailsAsync(mod.Display.Id, cancellationToken);

    /// <summary>
    /// Every build the author published, from the same provider the page is showing. Also not
    /// merged: the two shops number their uploads differently, and a list that interleaved them
    /// would read as one project releasing twice as often as it does.
    /// </summary>
    public Task<IReadOnlyList<ModVersion>> GetVersionsAsync(
        CatalogueMod mod, CancellationToken cancellationToken = default) =>
        SourceFor(mod.Display.Provider).GetVersionsAsync(mod.Display.Id, cancellationToken);

    private IModSource SourceFor(ModProvider provider) =>
        provider == ModProvider.CurseForge ? CurseForge : Modrinth;

    /// <summary>A hand-picked list, which only Modrinth can answer — the slugs are its own.</summary>
    public async Task<IReadOnlyList<CatalogueMod>> GetProjectsAsync(
        IReadOnlyList<string> slugs, CancellationToken cancellationToken = default) =>
        [.. (await Modrinth.GetProjectsAsync(slugs, cancellationToken).ConfigureAwait(false))
            .Select(listing => new CatalogueMod(listing, null))];

    /// <summary>
    /// Pairs the two lists up by name. Neither provider publishes the other's identifiers, so a
    /// normalised title is the only thing they have in common — "JourneyMap" and "Journeymap"
    /// are the same mod, and nothing in either API says so.
    /// </summary>
    private static IReadOnlyList<CatalogueMod> Merge(
        IReadOnlyList<ModListing> modrinth, IReadOnlyList<ModListing> curseForge, string text)
    {
        var byName = new Dictionary<string, ModListing>(StringComparer.Ordinal);
        foreach (var listing in curseForge) byName.TryAdd(CatalogueMod.KeyFor(listing.Title), listing);

        var paired = new HashSet<string>(StringComparer.Ordinal);
        var primary = new List<CatalogueMod>(modrinth.Count);

        foreach (var listing in modrinth)
        {
            var key = CatalogueMod.KeyFor(listing.Title);

            primary.Add(byName.TryGetValue(key, out var match) && paired.Add(key)
                ? new CatalogueMod(listing, match)
                : new CatalogueMod(listing, null));
        }

        var extras = curseForge
            .Where(listing => !paired.Contains(CatalogueMod.KeyFor(listing.Title)))
            .Select(listing => new CatalogueMod(null, listing))
            .ToList();

        var merged = Interleave(primary, extras);

        // Each provider ranks its own half and neither can see the other's, so the mod actually
        // named after the search can end up below one that merely mentions it. A stable sort by
        // how well the name matches fixes the top of the page without disturbing the rest.
        return text.Length == 0
            ? merged
            : [.. merged.OrderByDescending(mod => NameScore(mod.Title, text))];
    }

    /// <summary>
    /// How much a name looks like what was asked for: named exactly, starts with it, contains
    /// it, or neither. Searching for "create" should lead with Create, not with the most
    /// downloaded mod whose description happens to use the word.
    /// </summary>
    private static int NameScore(string name, string text)
    {
        if (name.Equals(text, StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.StartsWith(text, StringComparison.OrdinalIgnoreCase)) return 2;

        return name.Contains(text, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    /// <summary>Deals the two lists out together, keeping each one's own order intact.</summary>
    private static IReadOnlyList<CatalogueMod> Interleave(List<CatalogueMod> primary, List<CatalogueMod> extras)
    {
        if (extras.Count == 0) return primary;
        if (primary.Count == 0) return extras;

        var merged = new List<CatalogueMod>(primary.Count + extras.Count);
        int a = 0, b = 0;

        while (a < primary.Count || b < extras.Count)
        {
            for (var i = 0; i < ModrinthShare && a < primary.Count; i++) merged.Add(primary[a++]);

            if (b < extras.Count) merged.Add(extras[b++]);
        }

        return merged;
    }
}

/// <summary>
/// What came of an install. The two ways it can fail need saying differently: no build for this
/// Minecraft version is the player's problem to solve, an author who forbids third-party
/// downloads is nobody's.
/// </summary>
public sealed record ModInstallResult(
    string? FileName,
    ModProvider? From,
    bool Blocked = false,
    IReadOnlyList<string>? Dependencies = null,
    string? Reason = null)
{
    public bool Installed => FileName is { Length: > 0 };

    /// <summary>What came along with it, by file name. Empty for a mod that needs nothing.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = Dependencies ?? [];
}
