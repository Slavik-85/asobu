using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Asobu.App.Controls;
using Asobu.Core;
using Asobu.Core.Instances;
using Asobu.Core.Mods;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Asobu.App.ViewModels;

/// <summary>One tile in the browser: a search result plus what has happened to it since.</summary>
public partial class ModCard(CatalogueMod mod) : ViewModelBase
{
    public CatalogueMod Mod { get; } = mod;

    public string Title => Mod.Title;
    public string Summary => Mod.Summary;
    public string ProviderName => Mod.SourceLabel;
    public string DownloadsLabel => Mod.DownloadsLabel;

    /// <summary>Modrinth's bulk endpoint returns no author, so the line is dropped, not faked.</summary>
    public string AuthorLabel => HasAuthor ? $"By {Mod.Author}" : "";
    public bool HasAuthor => Mod.Author is { Length: > 0 };

    /// <summary>Everything worth knowing about a mod on one line, for the banner.</summary>
    public string MetaLine => HasAuthor
        ? $"{Mod.Author}  ·  {DownloadsLabel}  ·  {ProviderName}"
        : $"{DownloadsLabel}  ·  {ProviderName}";

    [ObservableProperty] public partial Bitmap? Icon { get; set; }

    /// <summary>
    /// A picture from the mod's own gallery, fetched only for the handful of cards the banner is
    /// about to show. Most mods have none, and downloading a full-size screenshot for every
    /// search result to display none of them would be a strange way to spend someone's
    /// connection.
    /// </summary>
    [ObservableProperty] public partial Bitmap? Art { get; set; }

    [ObservableProperty] public partial bool IsInstalling { get; set; }
    [ObservableProperty] public partial bool IsInstalled { get; set; }
    [ObservableProperty] public partial string? Notice { get; set; }

    public bool HasIcon => Icon is not null;
    public bool HasArt => Art is not null;
    public bool HasNotice => Notice is { Length: > 0 };
    public bool CanInstall => !IsInstalling && !IsInstalled;

    partial void OnIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasIcon));
    partial void OnArtChanged(Bitmap? value) => OnPropertyChanged(nameof(HasArt));
    partial void OnNoticeChanged(string? value) => OnPropertyChanged(nameof(HasNotice));
    partial void OnIsInstallingChanged(bool value) => OnPropertyChanged(nameof(CanInstall));
    partial void OnIsInstalledChanged(bool value) => OnPropertyChanged(nameof(CanInstall));
}

/// <summary>
/// A source the banner draws mods from. Three are orderings of the whole catalogue; the fourth is
/// a hand-picked list, which is why it carries slugs rather than only a sort.
/// </summary>
public sealed record ModShelf(string Title, ModSort Sort, IReadOnlyList<string>? Curated = null);

/// <summary>
/// One slide of the banner: a mod, and which shelf turned it up. The scenery comes along because
/// most mods have no gallery of their own and the banner still needs something behind it.
/// </summary>
public sealed record HeroPick(string Shelf, Bitmap? Scenery, ModCard Card);

/// <summary>A category chip under the banner.</summary>
public partial class CategoryChip(string name) : ViewModelBase
{
    public string Name { get; } = name;

    /// <summary>The provider ids are lowercase; the browser shows them capitalised.</summary>
    public string Label { get; } = char.ToUpperInvariant(name[0]) + name[1..];

    [ObservableProperty] public partial bool IsSelected { get; set; }
}

public partial class ExploreViewModel(
    AsobuLauncher launcher,
    Action<CatalogueMod> openPage,
    AskInstall askInstall) : ViewModelBase
{
    /// <summary>Long enough that typing a word doesn't fire a request per keystroke.</summary>
    private const int SearchDelayMilliseconds = 350;

    /// <summary>How many results one page of the grid asks each provider for.</summary>
    private const int GridLimit = 40;

    /// <summary>Modrinth's ceiling, and what the trending shelf sifts down to a handful.</summary>
    private const int WideLimit = 100;

    /// <summary>Roughly where a mod stops being one nobody has heard of.</summary>
    private const long TrendingFloor = 50_000;

    /// <summary>How many slides each shelf contributes per round of loading.</summary>
    private const int HeroPerShelf = 8;

    /// <summary>Fetch the next round this many slides before running out of the current one.</summary>
    private const int HeroLookahead = 6;

    /// <summary>
    /// Where the stream stops growing and starts going round again. Hundreds of mods and half an
    /// hour of rotation deep, which is past the point anyone is still watching — and endless
    /// really does have to mean endless somewhere, or the list grows for as long as the
    /// application is open.
    /// </summary>
    private const int HeroCap = 200;

    /// <summary>
    /// Roughly how wide the banner gets on a maximised window. Screenshots are published at
    /// 1080p and up, and a decoded 1920x1080 costs eight megabytes to show inside a strip
    /// 250 pixels tall.
    /// </summary>
    private const int BannerWidth = 1200;

    /// <summary>How many slides ahead keep their art loaded.</summary>
    private const int ArtWindow = 2;

    private const double RotateSeconds = 9;

    /// <summary>
    /// The mods almost every modded instance ends up wanting. Hand-picked rather than derived:
    /// no ranking captures "you will need this", and these are the ones people come back for.
    /// Long enough that the banner can keep drawing from it for several rounds.
    /// </summary>
    private static readonly string[] Essentials =
    [
        "fabric-api", "sodium", "lithium", "iris", "modmenu",
        "cloth-config", "ferrite-core", "entityculling", "jei", "journeymap",
        "appleskin", "roughly-enough-items", "create", "sodium-extra", "no-chat-reports",
        "architectury-api", "geckolib", "moonlight", "balm", "jade",
    ];

    private CancellationTokenSource? _search;
    private CancellationTokenSource? _hero;

    /// <summary>Everything already on the banner, so paging deeper never repeats a mod.</summary>
    private readonly HashSet<string> _seen = [];

    /// <summary>Which page of the grid has been fetched, so scrolling can ask for the next.</summary>
    private int _gridPage;

    private bool _loadingMore;
    private bool _gridExhausted;

    /// <summary>Titles already in the grid. Paging both providers at once turns up repeats.</summary>
    private readonly HashSet<string> _inGrid = [];

    private int _heroRound;
    private int _featureIndex;
    private bool _loadingHero;
    private bool _heroExhausted;
    private bool _onScreen = true;
    private DispatcherTimer? _rotate;

    public ObservableCollection<ModCard> Results { get; } = [];

    /// <summary>Minecraft versions to filter by, exactly as Browse offers them.</summary>
    public ObservableCollection<string> GameVersions { get; } = [];
    public ObservableCollection<CategoryChip> Categories { get; } = [];

    /// <summary>
    /// The banner's stream. It is never finished: each round adds another handful from every
    /// shelf, and the round after that is fetched well before this one runs out, so paging
    /// forward keeps turning up new mods rather than looping back to the first one.
    /// </summary>
    public ObservableCollection<HeroPick> Features { get; } = [];

    public IReadOnlyList<ModShelf> Shelves { get; } =
    [
        new("Most popular", ModSort.Popular),
        new("Most downloaded", ModSort.Downloads),
        new("Essentials", ModSort.Popular, Essentials),
        new("Trending", ModSort.Updated),
    ];

    /// <summary>
    /// Which Minecraft version the page is about.
    ///
    /// Explore is for finding out what exists, which is a question about a version rather than
    /// about one of your instances — and asking for an instance first meant a new install had
    /// an empty page and a note telling them to go and make something before they could look
    /// around. Where a mod ends up is asked when it is added, by the same sheet every other
    /// page uses.
    /// </summary>
    [ObservableProperty] public partial string? GameVersion { get; set; }

    /// <summary>The entry at the top of the version list. Not a version, so it never reaches a query.</summary>
    public const string AnyVersion = "All versions";

    /// <summary>
    /// What the version box shows.
    ///
    /// Kept apart from GameVersion so the "all versions" entry never leaks into a search as if it
    /// were a version number: picking it leaves GameVersion null, which every query here already
    /// reads as "any". They follow each other, so setting either one still moves the box.
    /// </summary>
    [ObservableProperty] public partial string? VersionChoice { get; set; }

    partial void OnVersionChoiceChanged(string? value) =>
        GameVersion = value == AnyVersion ? null : value;

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool IsSearching { get; set; }

    /// <summary>Shown at the foot of the grid while the next page is on its way.</summary>
    [ObservableProperty] public partial bool IsLoadingMore { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string? Category { get; set; }


    /// <summary>The slide showing right now.</summary>
    [ObservableProperty] public partial HeroPick? Feature { get; set; }

    /// <summary>
    /// True while the pointer is over the banner. Rotating a mod away from under someone who is
    /// reading about it, or reaching for its Add button, is how carousels earn their reputation.
    /// </summary>
    public bool HeroPaused { get; private set; }

    public void SetHeroPaused(bool paused) => HeroPaused = paused;

    public bool IsEmpty => Results.Count == 0 && !IsSearching;

    /// <summary>
    /// Without this the "nothing here" message keeps whatever value it had before the search
    /// started — so an empty page announces that it found nothing at the very moment it began
    /// looking, and goes on saying so until results arrive.
    /// </summary>
    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    public bool IsSearchingText => SearchText.Trim().Length > 0;

    /// <summary>Browsing furniture. A text search replaces what it offers.</summary>
    public bool ShowCategories => !IsSearchingText;

    /// <summary>The banner needs a mod to be about, and there is none until the first round lands.</summary>
    public bool ShowHero => ShowCategories && Feature is not null;

    /// <summary>Says what the grid underneath is showing, whichever route led there.</summary>
    public string ResultsHeading =>
        IsSearchingText ? $"Results for “{SearchText.Trim()}”"
        : Category is { Length: > 0 } category ? char.ToUpperInvariant(category[0]) + category[1..]
        : "Popular mods";

    /// <summary>
    /// Said quietly under the toolbar rather than as a page of its own: without a CurseForge key
    /// the browser still works, it just has one catalogue instead of two, and that is a footnote
    /// rather than a problem to stop and solve.
    /// </summary>
    public string? SourceNotice =>
        Catalogue.CurseForgeMissing
            ? "Showing Modrinth only — this build has no CurseForge key. Add one in Settings."
        : Catalogue.CurseForgeRejected
            ? "Showing Modrinth only — CurseForge turned this build's key down."
        : null;

    public bool HasSourceNotice => SourceNotice is not null;

    public string TargetLabel => GameVersion is { Length: > 0 } version
        ? $"Showing mods for Minecraft {version}"
        : "Showing mods for every version";

    private ModCatalogue Catalogue => launcher.Mods;

    /// <summary>The chosen category as the query wants it — a list, of nought or one.</summary>
    private IReadOnlyList<string>? Picked => Category is { Length: > 0 } category ? [category] : null;

    public void Reload()
    {
        if (GameVersions.Count == 0) _ = LoadGameVersionsAsync();
        if (Categories.Count == 0) _ = LoadCategoriesAsync();

        OnPropertyChanged(nameof(TargetLabel));
        OnPropertyChanged(nameof(ShowCategories));
        OnPropertyChanged(nameof(ShowHero));
        NoticeChanged();

        // Choosing a version usually starts both of these off already; these are the fallback
        // for the case where it resolved to the same version and raised nothing.
        if (Features.Count == 0 && !_loadingHero) RestartHero();
        if (Results.Count == 0) _ = SearchAsync();
    }

    /// <summary>
    /// Releases only, with the versions your instances actually run pushed to the front. The
    /// same rule Browse follows: snapshots would treble the list to offer versions almost
    /// nothing publishes for.
    /// </summary>
    private async Task LoadGameVersionsAsync()
    {
        var versions = new List<string>();

        try
        {
            var manifest = await launcher.Meta.GetManifestAsync();

            versions.AddRange(manifest.Versions.Where(v => v.IsRelease).Select(v => v.Id));
        }
        catch (Exception)
        {
            // Offline. The instances still say which versions are worth offering.
        }

        var instances = launcher.Instances.LoadAll();

        foreach (var instance in instances)
            if (!versions.Contains(instance.MinecraftVersion, StringComparer.OrdinalIgnoreCase))
                versions.Insert(0, instance.MinecraftVersion);

        GameVersions.Clear();

        // At the top, where someone who does not want to filter by version finds it first.
        GameVersions.Add(AnyVersion);
        foreach (var version in versions) GameVersions.Add(version);

        // Whatever the first instance runs, since that is the one most likely to be modded
        // next — and the newest release for someone who has no instances yet. Never the entry
        // above it: opening on "all versions" would bury the answer people came for.
        VersionChoice ??= instances.FirstOrDefault()?.MinecraftVersion ?? versions.FirstOrDefault();
    }

    // ---- The banner ----

    [RelayCommand]
    private void NextFeature() => MoveFeature(1, manual: true);

    [RelayCommand]
    private void PreviousFeature() => MoveFeature(-1, manual: true);

    /// <summary>
    /// Which way the banner last moved, so the view can slide the new mod in from the side it
    /// came from. Read as the slide changes rather than bound, which is why it raises nothing.
    /// </summary>
    public int SlideDirection { get; private set; } = 1;

    private void MoveFeature(int delta, bool manual = false)
    {
        if (Features.Count == 0) return;

        _featureIndex = (_featureIndex + delta + Features.Count) % Features.Count;

        // Set before the slide changes: the view reads it the moment Feature raises.
        SlideDirection = delta;
        Feature = Features[_featureIndex];

        // Wrapping is the safety net for a catalogue that has run out, or for the cap; normally
        // the next round has already arrived by the time anyone reaches the end of this one.
        if (_featureIndex >= Features.Count - HeroLookahead) _ = LoadHeroRoundAsync();

        PrefetchArt();

        // Having pressed next, you get the full nine seconds with the mod you chose rather than
        // whatever was left of the one before it.
        if (manual) RestartRotation();
    }

    private void RestartRotation()
    {
        if (_rotate is null || !_onScreen) return;

        _rotate.Stop();
        _rotate.Start();
    }

    private void RestartHero()
    {
        _hero?.Cancel();
        _hero = new CancellationTokenSource();

        Features.Clear();
        _seen.Clear();
        _heroRound = 0;
        _featureIndex = 0;
        _heroExhausted = false;

        // The round that was in flight belongs to the token just cancelled, and its own finally
        // will not clear this — see below. Without it the fresh round would be turned away as a
        // duplicate and the banner would never fill.
        _loadingHero = false;

        Feature = null;

        _ = LoadHeroRoundAsync();
    }

    /// <summary>
    /// Adds one round to the stream: a few mods from each shelf, dealt out in turn so the banner
    /// alternates between them rather than showing eight of one and then eight of the next.
    /// </summary>
    private async Task LoadHeroRoundAsync()
    {
        if (_loadingHero || _heroExhausted || Features.Count >= HeroCap) return;

        var request = _hero;
        if (request is null) return;

        _loadingHero = true;
        var round = _heroRound++;

        try
        {
            var shelves = new List<(ModShelf Shelf, IReadOnlyList<CatalogueMod> Listings)>();

            foreach (var shelf in Shelves)
                shelves.Add((shelf, await FetchShelfAsync(shelf, round, request.Token)));

            if (request.IsCancellationRequested) return;

            var added = 0;

            for (var i = 0; i < HeroPerShelf; i++)
            {
                foreach (var (shelf, listings) in shelves)
                {
                    if (i >= listings.Count) continue;

                    var mod = listings[i];
                    var listing = mod.Display;

                    // The shelves overlap heavily — the most downloaded mod is usually also the
                    // most popular one — and a banner that shows Sodium twice in four slides
                    // looks broken rather than emphatic.
                    if (!_seen.Add(CatalogueMod.KeyFor(listing.Title))) continue;

                    // Scenery picked from the mod rather than the shelf: most mods have no
                    // gallery of their own, and four in a row against the same picture reads as
                    // a banner that failed to load.
                    Features.Add(new HeroPick(
                        shelf.Title, Backdrops.Any(listing.Title), new ModCard(mod)));
                    added++;
                }
            }

            // Every shelf came back with nothing new, so there is no deeper to go.
            if (added == 0) _heroExhausted = true;

            if (Feature is null && Features.Count > 0)
            {
                _featureIndex = 0;
                Feature = Features[0];
                PrefetchArt();
                StartRotating();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // The banner stays hidden either way, but silence here once cost an afternoon:
            // a failed round looks exactly like one that simply has not arrived yet.
            Error = ex.Message;
        }
        finally
        {
            // Only if this round is still the current one: a round abandoned by RestartHero must
            // not report the replacement that took its place as finished.
            if (ReferenceEquals(_hero, request)) _loadingHero = false;
        }
    }

    private async Task<IReadOnlyList<CatalogueMod>> FetchShelfAsync(
        ModShelf shelf, int round, CancellationToken cancellationToken)
    {
        // The curated shelf is one fixed list, so rounds walk down it rather than asking again.
        if (shelf.Curated is { Count: > 0 } curated)
        {
            var slice = curated.Skip(round * HeroPerShelf).Take(HeroPerShelf).ToList();

            return slice.Count == 0
                ? []
                : await Catalogue.GetProjectsAsync(slice, cancellationToken);
        }

        // Trending needs a wide slice to sift, so it pages in hundreds where the others page in
        // handfuls — see Trending below for why.
        var wide = shelf.Sort == ModSort.Updated;
        var limit = wide ? WideLimit : HeroPerShelf;

        var listings = await Catalogue.SearchAsync(
            new ModQuery(
                "",
                GameVersion,
                null,
                shelf.Sort,
                Categories: null,
                Limit: limit,
                Offset: round * limit),
            cancellationToken);

        return wide ? [.. Trending(listings).Take(HeroPerShelf)] : listings;
    }

    /// <summary>
    /// Icons and gallery art for the slide showing and the next couple, so paging forward is not
    /// spent staring at an empty banner while a screenshot downloads — and art let go of again
    /// everywhere else, because a decoded screenshot is megabytes and the stream has no end.
    /// </summary>
    private void PrefetchArt()
    {
        var request = _hero;
        if (request is null) return;

        for (var i = 0; i < Features.Count; i++)
        {
            var card = Features[i].Card;
            var near = i >= _featureIndex - 1 && i <= _featureIndex + ArtWindow;

            if (!near)
            {
                card.Art = null;
                continue;
            }

            _ = LoadIconAsync(card, request.Token);
            _ = LoadArtAsync(card, request.Token);
        }
    }

    /// <summary>Starts the banner moving on its own, once there is something on it.</summary>
    private void StartRotating()
    {
        if (_rotate is null)
        {
            _rotate = new DispatcherTimer { Interval = TimeSpan.FromSeconds(RotateSeconds) };
            _rotate.Tick += (_, _) =>
            {
                if (!HeroPaused && ShowHero) MoveFeature(1);
            };
        }

        if (_onScreen) _rotate.Start();
    }

    /// <summary>
    /// Whether anyone can see the page. A banner nobody is looking at should not be quietly
    /// advancing every nine seconds, fetching a screenshot each time it does.
    ///
    /// Told by the shell rather than worked out from the view's own lifetime: the page is torn
    /// down and rebuilt on every navigation, and one view's teardown arriving after the next
    /// one's setup would stop the banner that had just been started.
    /// </summary>
    public void SetOnScreen(bool onScreen)
    {
        _onScreen = onScreen;

        if (onScreen) _rotate?.Start();
        else _rotate?.Stop();
    }

    // ---- Categories ----

    /// <summary>
    /// The shortcut row under the banner. Asked for rather than written out here, because both
    /// providers add categories and a list in the source would start going stale immediately.
    /// </summary>
    private async Task LoadCategoriesAsync()
    {
        try
        {
            foreach (var name in await Catalogue.GetShortcutCategoriesAsync(ModKind.Mod))
                Categories.Add(new CategoryChip(name));
        }
        catch (Exception)
        {
            // No shortcut row is a page with no shortcuts, not a broken page.
        }
    }

    [RelayCommand]
    private void PickCategory(CategoryChip? chip)
    {
        if (chip is null) return;

        // Clicking the selected chip again clears it, so a category is never a one-way door.
        SetCategory(chip.IsSelected ? null : chip.Name);
        OnPropertyChanged(nameof(ResultsHeading));
        _ = SearchAsync();
    }

    private void SetCategory(string? name)
    {
        Category = name;

        foreach (var chip in Categories)
            chip.IsSelected = name is not null && chip.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Filters ----

    partial void OnGameVersionChanged(string? value)
    {
        // Set from elsewhere — opening Browse on an instance, say — and the box has to follow.
        if (value is { Length: > 0 } && VersionChoice != value) VersionChoice = value;

        OnPropertyChanged(nameof(TargetLabel));
        RestartHero();
        _ = SearchAsync();
    }

    partial void OnFeatureChanged(HeroPick? value) => OnPropertyChanged(nameof(ShowHero));

    private void NoticeChanged()
    {
        OnPropertyChanged(nameof(SourceNotice));
        OnPropertyChanged(nameof(HasSourceNotice));
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsSearchingText));
        OnPropertyChanged(nameof(ShowCategories));
        OnPropertyChanged(nameof(ShowHero));
        OnPropertyChanged(nameof(ResultsHeading));
        _ = SearchAsync();
    }

    /// <summary>
    /// Debounced, and every run cancels the one before it — otherwise a fast typist gets results
    /// for a prefix landing after the results for the whole word.
    /// </summary>
    private async Task SearchAsync()
    {
        _search?.Cancel();
        var request = new CancellationTokenSource();
        _search = request;

        Error = null;

        try
        {
            await Task.Delay(SearchDelayMilliseconds, request.Token);

            IsSearching = true;

            var text = SearchText.Trim();

            var listings = await Catalogue.SearchAsync(
                new ModQuery(
                    text,
                    GameVersion,
                    null,
                    text.Length > 0 ? ModSort.Relevance : ModSort.Popular,
                    Picked,
                    GridLimit),
                request.Token);

            if (request.IsCancellationRequested) return;

            Results.Clear();
            _inGrid.Clear();
            _gridPage = 0;
            _gridExhausted = false;

            Append(listings, request.Token);

            NoticeChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!request.IsCancellationRequested) Error = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_search, request))
            {
                IsSearching = false;
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    /// <summary>
    /// Fetches the page after the one showing. Called as the page nears the bottom of the
    /// scroller, so the grid runs out only when the catalogue does.
    /// </summary>
    public void LoadMore() => _ = LoadMoreAsync();

    private async Task LoadMoreAsync()
    {
        // Nothing to extend yet, nothing left to fetch, or a search is already replacing the lot.
        if (_loadingMore || _gridExhausted || IsSearching || Results.Count == 0) return;

        var request = _search;
        if (request is null) return;

        _loadingMore = true;
        IsLoadingMore = true;

        try
        {
            var page = _gridPage + 1;
            var text = SearchText.Trim();

            var listings = await Catalogue.SearchAsync(
                new ModQuery(
                    text,
                    GameVersion,
                    null,
                    text.Length > 0 ? ModSort.Relevance : ModSort.Popular,
                    Picked,
                    GridLimit,
                    page * GridLimit),
                request.Token);

            if (request.IsCancellationRequested || !ReferenceEquals(_search, request)) return;

            _gridPage = page;

            // Exhausted means the provider had nothing left, not that everything it sent was
            // already on the page — paging two catalogues at once repeats mods where the pairing
            // shifts between pages, and stopping on that would end the grid early. A page that
            // adds nothing new simply leaves the next scroll to reach further.
            if (listings.Count == 0) _gridExhausted = true;

            Append(listings, request.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // A page that failed to arrive is a grid that stops growing, not a broken page. The
            // next scroll to the bottom tries again.
        }
        finally
        {
            _loadingMore = false;
            IsLoadingMore = false;
        }
    }

    /// <summary>
    /// Adds what is new to the grid and starts its icons loading. Returns how many were added,
    /// which is how paging knows it has reached the end.
    /// </summary>
    private int Append(IReadOnlyList<CatalogueMod> listings, CancellationToken cancellationToken)
    {
        var added = 0;

        foreach (var mod in listings)
        {
            if (!_inGrid.Add(CatalogueMod.KeyFor(mod.Title))) continue;

            var card = new ModCard(mod);
            Results.Add(card);
            _ = LoadIconAsync(card, cancellationToken);
            added++;
        }

        if (added > 0) OnPropertyChanged(nameof(IsEmpty));

        return added;
    }

    /// <summary>
    /// Turns "recently updated" into something worth calling trending. Neither provider has a
    /// trending index, and recency on its own hands the page whichever hobby project was touched
    /// an hour ago — the shelf filled up with mods on three thousand downloads. Taking a wide
    /// slice of the freshly-updated and keeping the ones people actually use leaves the order
    /// alone, so it still reads as what is being worked on rather than a second popularity list.
    ///
    /// If almost nothing clears the bar — a niche loader, an old Minecraft version — the
    /// unfiltered list goes through instead. A thin shelf beats an empty one.
    /// </summary>
    private static IReadOnlyList<CatalogueMod> Trending(IReadOnlyList<CatalogueMod> listings)
    {
        var known = listings.Where(mod => mod.Downloads >= TrendingFloor).ToList();

        return known.Count >= HeroPerShelf ? known : listings;
    }

    /// <summary>
    /// The banner's backdrop. Scaled down on the way in and deliberately not put in the shared
    /// image cache: these are published at full screenshot size, they are shown inside a strip a
    /// quarter of that tall, and the stream keeps finding more of them. Failure just leaves the
    /// shelf's own scenery showing.
    /// </summary>
    private async Task LoadArtAsync(ModCard card, CancellationToken cancellationToken)
    {
        if (card.Art is not null || card.Mod.GalleryUrl is not { Length: > 0 } url) return;

        try
        {
            var bytes = await launcher.Web.GetAsync(url, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

            using var stream = new MemoryStream(bytes);
            var art = Bitmap.DecodeToWidth(stream, BannerWidth);

            await Dispatcher.UIThread.InvokeAsync(() => card.Art = art);
        }
        catch (Exception)
        {
        }
    }

    private async Task LoadIconAsync(ModCard card, CancellationToken cancellationToken)
    {
        if (card.Icon is not null || card.Mod.IconUrl is not { Length: > 0 } url) return;

        try
        {
            var bytes = await launcher.Web.GetAsync(url, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

            var icon = ImageCache.FromBytes(url, bytes);
            await Dispatcher.UIThread.InvokeAsync(() => card.Icon = icon);
        }
        catch (Exception)
        {
            // A missing logo is a gap in a tile, never a reason to fail the search.
        }
    }

    [RelayCommand]
    private void Install(ModCard? card)
    {
        if (card is null) return;

        askInstall(
            $"Install {card.Title}",
            instance => ModInstall.RunAsync(launcher, instance, card),
            // Every build this mod has, so the sheet can say which instances can take it.
            async token => ModSupport.From(await launcher.Mods.GetVersionsAsync(card.Mod, token)),
            card.Mod);
    }

    /// <summary>
    /// Opens the mod's own page rather than its page on the web. The web link is still there, on
    /// the page itself, where it belongs — leaving the launcher should be a choice, not what
    /// clicking a search result does.
    /// </summary>
    [RelayCommand]
    private void OpenPage(ModCard? card)
    {
        if (card is not null) openPage(card.Mod);
    }

}
