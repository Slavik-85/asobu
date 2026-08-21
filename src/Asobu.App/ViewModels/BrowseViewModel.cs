using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Asobu.App.Controls;
using Asobu.Core;
using Asobu.Core.Instances;
using Asobu.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Asobu.App.ViewModels;

/// <summary>What is being looked for, as the picker offers it.</summary>
public sealed record KindOption(ModKind Value, string Label);

public sealed record SortOption(ModSort Value, string Label);

/// <summary>One tickable category in the filter panel.</summary>
public partial class CategoryFilter(string name, Action changed) : ViewModelBase
{
    public string Name { get; } = name;

    [ObservableProperty] public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>
/// The catalogue as a place to rummage through rather than a place to be shown things: pick what
/// kind of thing you are after, narrow it by category, order it how you like. Kept apart from the
/// Discover side, which answers a different question — that one decides what to show you, this
/// one does what it is told.
/// </summary>
public partial class BrowseViewModel(
    AsobuLauncher launcher,
    Action<CatalogueMod> openPage,
    AskInstall askInstall,
    AskCreatePack askCreatePack) : ViewModelBase
{
    /// <summary>
    /// The instance this browser is shopping for, or null when it is the catalogue at large.
    ///
    /// With one set, three questions stop being asked because they already have answers: which
    /// Minecraft version to look against, which loader, and which instance an Add goes to. What
    /// is left is a list of things that will work here, and one button that puts them in.
    /// </summary>
    public Instance? Target { get; private set; }

    public bool IsInstanceScoped => Target is not null;

    /// <summary>
    /// What the target instance already has, so a result that is already in it says Added rather
    /// than offering to add it again. Empty until the scan comes back, and empty for the
    /// unscoped browser, which has no instance to compare against.
    /// </summary>
    private InstalledMods _installed = InstalledMods.Empty;

    /// <summary>Where closing this browser goes back to. Only set on the scoped one.</summary>
    private Action? _back;

    /// <summary>
    /// Points the browser at one instance and starts it fresh. Fresh on purpose: the previous
    /// visit may have been for a different instance, and a Fabric 1.20 search left standing in
    /// front of a NeoForge 1.21 instance is worse than a moment's loading.
    /// </summary>
    public void OpenFor(Instance instance, ModKind kind, Action back)
    {
        var wanted = Kinds.FirstOrDefault(option => option.Value == kind) ?? Kinds[0];
        var changed = Target?.Id != instance.Id || Kind?.Value != wanted.Value;

        Target = instance;
        _back = back;
        Kind = wanted;

        OnPropertyChanged(nameof(IsInstanceScoped));
        OnPropertyChanged(nameof(BackLabel));

        GameVersion = instance.MinecraftVersion;

        Sort ??= Sorts[0];

        if (changed)
        {
            SearchText = "";
            foreach (var category in Categories) category.IsChecked = false;
            Results.Clear();
        }

        if (Categories.Count == 0) _ = LoadCategoriesAsync();
        if (Results.Count == 0) _ = SearchAsync();

        _ = RefreshInstalledAsync();
    }

    /// <summary>
    /// Rereads what the instance has and marks the results accordingly.
    ///
    /// Run again after each add rather than trusted from the first scan: a mod that brought
    /// dependencies with it put several files in the folder, and any of them might be further
    /// down the same list of search results.
    /// </summary>
    private async Task RefreshInstalledAsync()
    {
        if (Target is not { } target)
        {
            _installed = InstalledMods.Empty;
            return;
        }

        // Reading forty jars is not something to do on the thread drawing the list.
        _installed = await Task.Run(() => InstalledMods.For(launcher.Paths, target));

        foreach (var card in Results)
            if (!card.IsInstalled && _installed.Has(card.Mod))
                card.IsInstalled = true;
    }

    [RelayCommand]
    private void Close() => _back?.Invoke();

    private const int SearchDelayMilliseconds = 350;
    private const int PageSize = 40;

    private CancellationTokenSource? _search;
    private CancellationTokenSource? _categories;

    /// <summary>Titles already listed. Paging both providers at once turns up repeats.</summary>
    private readonly HashSet<string> _listed = [];

    private int _page;
    private bool _loadingMore;
    private bool _exhausted;

    public ObservableCollection<ModCard> Results { get; } = [];
    public ObservableCollection<CategoryFilter> Categories { get; } = [];

    /// <summary>
    /// Minecraft versions to look mods up against. A plain list of versions rather than a list of
    /// instances: browsing is a question about the catalogue, and two instances both called after
    /// the version they run made for a picker that repeated itself.
    /// </summary>
    public ObservableCollection<string> GameVersions { get; } = [];

    private IReadOnlyList<Instance> _instances = [];

    public IReadOnlyList<KindOption> Kinds { get; } =
    [
        new(ModKind.Mod, "Mods"),
        new(ModKind.Modpack, "Modpacks"),
        new(ModKind.ResourcePack, "Resource Packs"),
        new(ModKind.Shader, "Shaders"),
        new(ModKind.DataPack, "Data Packs"),
        new(ModKind.World, "Worlds"),
        new(ModKind.Any, "Everything"),
    ];

    /// <summary>
    /// Best match leads, and is the default. Sorting a search by popularity is how looking for
    /// "create" turns up Xaero's Minimap: it is the more popular mod and the word appears in its
    /// description. With an empty box there is nothing to match against, so it means popularity
    /// anyway — see <see cref="QueryFor"/>.
    /// </summary>
    public IReadOnlyList<SortOption> Sorts { get; } =
    [
        new(ModSort.Relevance, "Best match"),
        new(ModSort.Popular, "Popularity"),
        new(ModSort.Downloads, "Total downloads"),
        new(ModSort.Updated, "Recently updated"),
        new(ModSort.Newest, "Newest"),
    ];

    /// <summary>Which Minecraft version the results are for.</summary>
    [ObservableProperty] public partial string? GameVersion { get; set; }

    [ObservableProperty] public partial KindOption? Kind { get; set; }
    [ObservableProperty] public partial SortOption? Sort { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool IsSearching { get; set; }
    [ObservableProperty] public partial bool IsLoadingMore { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }

    /// <summary>Whether the filter drawer is out.</summary>
    [ObservableProperty] public partial bool ShowFilters { get; set; } = true;

    /// <summary>
    /// Held open for the length of the slide on the way out. Dropping the panel the moment it is
    /// dismissed would take it off screen before a single frame of the animation had drawn.
    /// </summary>
    [ObservableProperty] public partial bool IsFiltersClosing { get; set; }

    /// <summary>
    /// Whether the drawer should be taking up room. Not the same as being on screen: while it
    /// slides out it is still mounted, and the results need to start reclaiming the width at the
    /// same moment rather than waiting for it to finish.
    /// </summary>
    public bool FiltersOut => ShowFilters && !IsFiltersClosing;

    partial void OnShowFiltersChanged(bool value) => OnPropertyChanged(nameof(FiltersOut));
    partial void OnIsFiltersClosingChanged(bool value) => OnPropertyChanged(nameof(FiltersOut));

    public bool IsEmpty => Results.Count == 0 && !IsSearching;

    /// <summary>
    /// Raised with the flag it depends on. Otherwise "nothing found" keeps the value it had
    /// before the search started, and sits under the word "Searching" contradicting it.
    /// </summary>
    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    public string TargetLabel => GameVersion is { Length: > 0 } version
        ? $"Showing what works on Minecraft {version}"
        : "Pick a Minecraft version";

    /// <summary>Named after where it goes, which is the instance this was opened from.</summary>
    public string BackLabel => Target is { } instance ? instance.Name : "Back";
    public bool HasCategories => Categories.Count > 0;
    public bool HasPicked => Categories.Any(category => category.IsChecked);

    /// <summary>
    /// What is on the page, rather than what exists. Neither provider agrees with the other on a
    /// total, and adding two of them together would double-count everything both carry.
    /// </summary>
    public string CountLabel => Results.Count switch
    {
        0 when IsSearching => "Searching…",
        0 => "Nothing found",
        1 => "1 result",
        var n => $"{n.ToString("N0", CultureInfo.InvariantCulture)} results",
    };

    public void Reload()
    {
        _instances = launcher.Instances.LoadAll();

        Kind ??= Kinds[0];
        Sort ??= Sorts[0];

        if (GameVersions.Count == 0) _ = LoadGameVersionsAsync();

        OnPropertyChanged(nameof(TargetLabel));

        if (Categories.Count == 0) _ = LoadCategoriesAsync();
        if (Results.Count == 0) _ = SearchAsync();
    }

    /// <summary>
    /// Releases only. Snapshots would triple the list to offer versions almost nothing publishes
    /// for, and the versions people already run are on the list regardless — those come from the
    /// instances themselves.
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
            // Offline: the instances still say which versions are worth offering.
        }

        foreach (var instance in _instances)
            if (!versions.Contains(instance.MinecraftVersion, StringComparer.OrdinalIgnoreCase))
                versions.Insert(0, instance.MinecraftVersion);

        GameVersions.Clear();
        foreach (var version in versions) GameVersions.Add(version);

        // Whatever the first instance runs, since that is the one most likely to be modded next.
        GameVersion ??= _instances.FirstOrDefault()?.MinecraftVersion ?? GameVersions.FirstOrDefault();
    }

    partial void OnGameVersionChanged(string? value)
    {
        OnPropertyChanged(nameof(TargetLabel));

        // The version is part of every query, so a different one is a different search.
        if (Results.Count > 0) _ = SearchAsync();
    }

    // ---- Filters ----

    partial void OnKindChanged(KindOption? value)
    {
        // The categories belong to the kind — a resource pack is 64x, a mod is not — so the
        // panel is rebuilt rather than left showing filters that no longer apply.
        _ = LoadCategoriesAsync();
        _ = SearchAsync();
    }

    partial void OnSortChanged(SortOption? value) => _ = SearchAsync();

    partial void OnSearchTextChanged(string value) => _ = SearchAsync();

    [RelayCommand]
    private void ClearFilters()
    {
        foreach (var category in Categories) category.IsChecked = false;
    }

    /// <summary>Matches the drawer's slide in Asobu.axaml; keep the two in step.</summary>
    private const int DrawerSlideMilliseconds = 260;

    [RelayCommand]
    private async Task ToggleFiltersAsync()
    {
        if (IsFiltersClosing) return;

        if (!ShowFilters)
        {
            ShowFilters = true;
            return;
        }

        IsFiltersClosing = true;
        await Task.Delay(DrawerSlideMilliseconds);

        ShowFilters = false;
        IsFiltersClosing = false;
    }

    private void CategoryPicked()
    {
        OnPropertyChanged(nameof(HasPicked));
        _ = SearchAsync();
    }

    private async Task LoadCategoriesAsync()
    {
        _categories?.Cancel();
        var request = new CancellationTokenSource();
        _categories = request;

        Categories.Clear();
        OnPropertyChanged(nameof(HasCategories));
        OnPropertyChanged(nameof(HasPicked));

        try
        {
            var names = await launcher.Mods.GetCategoriesAsync(
                Kind?.Value ?? ModKind.Mod, request.Token);

            if (request.IsCancellationRequested || !ReferenceEquals(_categories, request)) return;

            foreach (var name in names) Categories.Add(new CategoryFilter(name, CategoryPicked));

            OnPropertyChanged(nameof(HasCategories));
        }
        catch (Exception ex)
        {
            // No filter panel is a browser with no filters, not a broken page — but saying so
            // beats an empty panel that reads as "this kind has none".
            Error = ex.Message;
        }
    }

    // ---- Searching ----

    private IReadOnlyList<string>? Picked
    {
        get
        {
            var picked = Categories.Where(c => c.IsChecked).Select(c => c.Name).ToList();

            return picked.Count > 0 ? picked : null;
        }
    }

    private ModQuery QueryFor(int page)
    {
        var text = SearchText.Trim();

        return new ModQuery(
            text,
            GameVersion,
            // Browsing for one instance is a question about that instance, so the loader is part
            // of it. Without a target this stays null: a version is not an instance, and a mod
            // that turns out not to build for the loader says so when Add is pressed.
            Target is { IsModded: true } target ? target.Loader : null,
            // Best match means nothing without something to match against, so an empty box falls
            // back to whatever the picker says rather than returning an arbitrary slice.
            text.Length == 0 && Sort?.Value == ModSort.Relevance ? ModSort.Popular : Sort?.Value ?? ModSort.Popular,
            Picked,
            PageSize,
            page * PageSize,
            Kind?.Value ?? ModKind.Mod);
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
            OnPropertyChanged(nameof(CountLabel));

            var listings = await launcher.Mods.SearchAsync(QueryFor(0), request.Token);

            if (request.IsCancellationRequested) return;

            Results.Clear();
            _listed.Clear();
            _page = 0;
            _exhausted = false;

            Append(listings, request.Token);
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
                OnPropertyChanged(nameof(CountLabel));
            }
        }
    }

    /// <summary>Fetches the page after the one showing, as the list nears its end.</summary>
    public void LoadMore() => _ = LoadMoreAsync();

    private async Task LoadMoreAsync()
    {
        if (_loadingMore || _exhausted || IsSearching || Results.Count == 0) return;

        var request = _search;
        if (request is null) return;

        _loadingMore = true;
        IsLoadingMore = true;

        try
        {
            var page = _page + 1;
            var listings = await launcher.Mods.SearchAsync(QueryFor(page), request.Token);

            if (request.IsCancellationRequested || !ReferenceEquals(_search, request)) return;

            _page = page;

            // Exhausted means the providers had nothing left, not that everything they sent was
            // already listed — paging two catalogues at once repeats entries where the pairing
            // shifts between pages, and stopping on that would end the list early.
            if (listings.Count == 0) _exhausted = true;

            Append(listings, request.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // A page that failed to arrive is a list that stops growing. Scrolling tries again.
        }
        finally
        {
            _loadingMore = false;
            IsLoadingMore = false;
        }
    }

    private void Append(IReadOnlyList<CatalogueMod> listings, CancellationToken cancellationToken)
    {
        foreach (var mod in listings)
        {
            if (!_listed.Add(CatalogueMod.KeyFor(mod.Title))) continue;

            // Already in this instance, so the tile says Added and offers no button — the same
            // state a tile reaches after being installed from here.
            var card = new ModCard(mod) { IsInstalled = _installed.Has(mod) };

            Results.Add(card);
            _ = LoadIconAsync(card, cancellationToken);
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountLabel));
    }

    private async Task LoadIconAsync(ModCard card, CancellationToken cancellationToken)
    {
        if (card.Icon is not null || card.Mod.IconUrl is not { Length: > 0 } url) return;

        try
        {
            var bytes = await launcher.Web.GetAsync(url, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

            var icon = ImageCache.FromBytes(url, bytes);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => card.Icon = icon);
        }
        catch (Exception)
        {
            // A missing logo is a gap in a row, never a reason to fail the search.
        }
    }

    [RelayCommand]
    private async Task InstallAsync(ModCard? card)
    {
        if (card is null) return;

        // A pack is not something to put into an instance — it is one. Even scoped to an
        // instance, installing a modpack means making another, so this comes first.
        if (card.Mod.IsPack)
        {
            askCreatePack(card.Mod, null);
            return;
        }

        // Browsing on an instance's behalf: there is no instance left to pick and no version to
        // choose, so Add means add, and the newest build that runs here is what that is.
        if (Target is { } target)
        {
            await ModInstall.RunAsync(launcher, target, card);

            // Dependencies came in alongside it, and any of them may be further down this list.
            await RefreshInstalledAsync();
            return;
        }

        askInstall(
            $"Install {card.Title}",
            instance => ModInstall.RunAsync(launcher, instance, card),
            // Every build this mod has, so the sheet can say which instances can take it.
            async token => ModSupport.From(await launcher.Mods.GetVersionsAsync(card.Mod, token)),
            card.Mod);
    }

    /// <summary>See the note on Explore's: this opens the mod's page inside Asobu.</summary>
    /// <remarks>The page inherits the scope, so its versions are the ones this instance runs.</remarks>
    [RelayCommand]
    private void OpenPage(ModCard? card)
    {
        if (card is not null) openPage(card.Mod);
    }
}
