using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Asobu.App.Controls;
using Asobu.Core;
using Asobu.Core.Instances;
using Asobu.Core.Minecraft;
using Asobu.Core.Mods;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Asobu.App.ViewModels;

/// <summary>One picture from a mod's gallery, once it has been fetched.</summary>
public partial class GalleryShot(ModImage image) : ViewModelBase
{
    public string Url { get; } = image.Url;
    public string ThumbUrl { get; } = image.ThumbUrl;

    /// <summary>
    /// True for the picture the banner at the top of the page is showing. Only these are ever
    /// candidates for being dropped from the gallery: a screenshot is worth seeing twice, a
    /// header plate is not.
    /// </summary>
    public bool IsBannerSource { get; init; }

    /// <summary>
    /// Whether the picture was made to be a banner rather than to be looked at. Authors say so
    /// in the name far more often than not — "jei-header", "banner", "title card" — and where
    /// they do not, the shape gives it away.
    /// </summary>
    public bool NamedAsBanner =>
        Mentions(Title) || Mentions(System.IO.Path.GetFileNameWithoutExtension(Url));

    /// <summary>
    /// What authors call these when they name them. Matched against a form with the punctuation
    /// taken out, so "title-card", "Title Card" and "titlecard" are all the same word — which is
    /// most of the value, since no two authors separate words the same way.
    /// </summary>
    private static readonly string[] BannerWords =
        ["banner", "header", "cover", "titlecard", "splash", "logo", "wordmark"];

    private static bool Mentions(string? text)
    {
        if (text is not { Length: > 0 }) return false;

        var flattened = new string([.. text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

        return BannerWords.Any(word => flattened.Contains(word, StringComparison.Ordinal));
    }

    /// <summary>
    /// Far wider than a screenshot ever is. 16:9 is 1.78 and an ultrawide capture about 2.4, so
    /// the line sits above both — a picture past it was cut to sit across the top of a page.
    /// </summary>
    public bool ShapedAsBanner =>
        Picture is { } picture && picture.Size.Height > 0 && picture.Size.Width / picture.Size.Height >= 2.6;
    public string? Title { get; } = image.Title;
    public bool HasTitle => Title is { Length: > 0 };

    /// <summary>Column-sized, which is all the page itself ever shows.</summary>
    [ObservableProperty] public partial Bitmap? Picture { get; set; }

    /// <summary>Set instead of a still when the picture turns out to move.</summary>
    [ObservableProperty] public partial AnimatedFrames? Frames { get; set; }

    [ObservableProperty] public partial AnimatedFrames? LargeFrames { get; set; }

    /// <summary>The frames the viewer should play: its own where it has them, else the thumbnail's.</summary>
    public AnimatedFrames? BestFrames => LargeFrames ?? Frames;

    /// <summary>
    /// Fetched only when the picture is opened full size. Most are never looked at that closely,
    /// and a gallery decoded at viewer resolution is tens of megabytes to show a thumbnail.
    /// </summary>
    [ObservableProperty] public partial Bitmap? Large { get; set; }

    /// <summary>The best there is so far — the thumbnail holds the frame until the big one lands.</summary>
    public Bitmap? Best => Large ?? Picture;

    public bool HasPicture => Picture is not null || Frames is not null;

    partial void OnPictureChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasPicture));
        OnPropertyChanged(nameof(Best));
    }

    partial void OnLargeChanged(Bitmap? value) => OnPropertyChanged(nameof(Best));

    partial void OnFramesChanged(AnimatedFrames? value)
    {
        OnPropertyChanged(nameof(HasPicture));
        OnPropertyChanged(nameof(BestFrames));
    }

    partial void OnLargeFramesChanged(AnimatedFrames? value) => OnPropertyChanged(nameof(BestFrames));
}

/// <summary>Which column the versions table is ordered by.</summary>
public enum VersionSort
{
    Released,
    GameVersion,
    Channel,
}

/// <summary>One page number in the strip under the table.</summary>
public sealed record PageNumber(int Number, bool IsCurrent);

/// <summary>One row in the versions list, and whether it has been installed yet.</summary>
public partial class ModVersionRow(ModVersion version) : ViewModelBase
{
    public ModVersion Version { get; } = version;

    public string Name => Version.Name;
    public string GameVersionLabel => Version.GameVersionLabel;
    public string LoaderLabel => Version.LoaderLabel;
    public string PublishedLabel => Version.PublishedLabel;
    public string SizeLabel => Version.SizeLabel;
    public string ChannelLabel => Version.ChannelLabel;
    public string ChannelInitial => Version.ChannelInitial;
    public bool IsRelease => Version.Channel == ModChannel.Release;
    public bool IsBeta => Version.Channel == ModChannel.Beta;
    public bool IsAlpha => Version.Channel == ModChannel.Alpha;
    public bool CanDownload => Version.CanDownload && !IsInstalling && !IsInstalled;

    [ObservableProperty] public partial bool IsInstalling { get; set; }
    [ObservableProperty] public partial bool IsInstalled { get; set; }
    [ObservableProperty] public partial string? Notice { get; set; }

    public bool HasNotice => Notice is { Length: > 0 };

    partial void OnNoticeChanged(string? value) => OnPropertyChanged(nameof(HasNotice));
    partial void OnIsInstallingChanged(bool value) => OnPropertyChanged(nameof(CanDownload));
    partial void OnIsInstalledChanged(bool value) => OnPropertyChanged(nameof(CanDownload));
}

/// <summary>
/// A mod's own page: what it is, what it looks like, and every build its author has published.
/// Reached from a card on either browsing page, and it keeps a way back to whichever one that
/// was — a page you can only leave by picking something else from the sidebar is a dead end.
/// </summary>
public partial class ModPageViewModel(
    AsobuLauncher launcher, AskInstall askInstall, AskCreatePack askCreatePack) : ViewModelBase
{
    /// <summary>
    /// The instance the page was opened on behalf of, when it was opened from that instance's
    /// own browser. Everything the page offers then narrows to what that instance can take:
    /// the versions table lists only builds it runs, and the buttons stop asking where to put
    /// things.
    /// </summary>
    public Instance? Target { get; private set; }

    public bool IsInstanceScoped => Target is not null;

    /// <summary>
    /// Whether the versions table's Loader column says anything. Only mods and packs are built
    /// against a loader; a resource pack's is "minecraft" and a shader's is "iris", which is a
    /// column of noise. Unknown kinds keep it — a column too many beats a fact withheld.
    /// </summary>
    public bool ShowsLoader => Card?.Mod.Kind is not (ModKind.ResourcePack or ModKind.Shader
                                                      or ModKind.DataPack or ModKind.World);

    /// <summary>A whole pack, which Download turns into an instance rather than adding to one.</summary>
    public bool IsPack => Card?.Mod.IsPack ?? false;

    /// <summary>Says so above the versions table, since the list is shorter than the mod's.</summary>
    public string VersionScopeLabel => Target is { } instance && !IsPack
        ? $"Only builds for {instance.LoaderName} {instance.MinecraftVersion}"
        : "";

    /// <summary>Wide enough for the banner on a maximised window, without decoding a 4K plate.</summary>
    private const int BannerWidth = 1400;

    /// <summary>The gallery is a column beside the description.</summary>
    private const int ShotWidth = 700;

    /// <summary>And this is the lightbox, which is most of a screen.</summary>
    private const int ViewerWidth = 1800;

    /// <summary>Matches the viewer's fade in Asobu.axaml; keep the two in step.</summary>
    private const int ViewerMilliseconds = 200;

    private CancellationTokenSource? _load;
    private Action? _back;

    /// <summary>How many builds one page of the table holds.</summary>
    private const int PageSize = 20;

    /// <summary>Every build there is; <see cref="Versions"/> is the page of it being shown.</summary>
    private readonly List<ModVersionRow> _allVersions = [];

    public ObservableCollection<GalleryShot> Gallery { get; } = [];
    public ObservableCollection<ModVersionRow> Versions { get; } = [];
    public ObservableCollection<PageNumber> Pages { get; } = [];

    [ObservableProperty] public partial ModCard? Card { get; set; }

    /// <summary>
    /// What the page is looking at decides what its table shows and what its buttons do, and
    /// both of those are read off the card. Announced here rather than from Load, which knows
    /// the target several lines before the card it belongs to exists.
    /// </summary>
    partial void OnCardChanged(ModCard? value)
    {
        OnPropertyChanged(nameof(ShowsLoader));
        OnPropertyChanged(nameof(IsPack));
        OnPropertyChanged(nameof(VersionScopeLabel));
    }
    /// <summary>The description as blocks to lay out, rather than as one wall of text.</summary>
    public ObservableCollection<ProseBlock> Description { get; } = [];
    [ObservableProperty] public partial bool IsLoading { get; set; }
    /// <summary>Which tab is showing. Overview is what opens.</summary>
    [ObservableProperty] public partial bool IsVersionsTab { get; set; }

    [ObservableProperty] public partial bool IsLoadingVersions { get; set; }
    [ObservableProperty] public partial VersionSort Sort { get; set; } = VersionSort.Released;

    /// <summary>Newest, highest and most finished first — the useful end of every column.</summary>
    [ObservableProperty] public partial bool IsDescending { get; set; } = true;

    [ObservableProperty] public partial int Page { get; set; }

    /// <summary>
    /// Drives the slide. Set a beat after the page is loaded rather than with it, so the class
    /// goes from absent to present with the view already built — an animation fires when its
    /// selector starts matching, and one that already matched when the control appeared has
    /// nothing to fire on.
    /// </summary>
    [ObservableProperty] public partial bool IsOpen { get; set; }

    [ObservableProperty] public partial bool IsClosing { get; set; }

    // ---- The lightbox ----

    [ObservableProperty] public partial bool IsViewerOpen { get; set; }
    [ObservableProperty] public partial bool IsViewerClosing { get; set; }
    [ObservableProperty] public partial GalleryShot? ViewerShot { get; set; }

    private int _viewerIndex;

    /// <summary>Which of how many, the way every gallery says it.</summary>
    public string ViewerCounter => Gallery.Count == 0 ? "" : $"{_viewerIndex + 1}/{Gallery.Count}";

    /// <summary>No arrows for a gallery of one; there is nowhere for them to go.</summary>
    public bool HasManyShots => Gallery.Count > 1;

    /// <summary>The banner behind the top of the page: the mod's own art where it has any.</summary>
    [ObservableProperty] public partial Bitmap? Banner { get; set; }

    public string Title => Card?.Title ?? "";
    public string AuthorLabel => Card?.AuthorLabel ?? "";
    public bool HasAuthor => Card?.HasAuthor ?? false;
    public string MetaLine => Card?.MetaLine ?? "";
    public Bitmap? Icon => Card?.Icon;
    public bool HasIcon => Card?.Icon is not null;
    public bool HasBanner => Banner is not null;
    public bool HasGallery => Gallery.Count > 0;

    public bool HasDescription => Description.Count > 0;
    public bool HasVersions => Versions.Count > 0;

    public bool IsOverviewTab => !IsVersionsTab;

    public int PageCount => Math.Max(1, (_allVersions.Count + PageSize - 1) / PageSize);
    public bool HasPages => PageCount > 1;
    public bool CanGoBackAPage => Page > 0;
    public bool CanGoForwardAPage => Page < PageCount - 1;

    /// <summary>An arrow on the column being sorted by, pointing the way it is sorted.</summary>
    public string ReleasedArrow => ArrowFor(VersionSort.Released);
    public string GameVersionArrow => ArrowFor(VersionSort.GameVersion);
    public string ChannelArrow => ArrowFor(VersionSort.Channel);

    private string ArrowFor(VersionSort column) =>
        Sort != column ? "" : IsDescending ? " ▾" : " ▴";

    public string CountLabel => _allVersions.Count switch
    {
        0 => "",
        1 => "1 build",
        var n => $"{n} builds",
    };

    /// <summary>Opens the page on a mod, remembering how to get back where it was opened from.</summary>
    public void Load(CatalogueMod mod, Action back, Instance? target = null)
    {
        Target = target;
        OnPropertyChanged(nameof(IsInstanceScoped));

        _load?.Cancel();
        _load = new CancellationTokenSource();
        var request = _load;

        _back = back;

        Card = new ModCard(mod);
        Description.Clear();
        Banner = null;

        IsClosing = false;
        IsOpen = false;
        IsViewerOpen = false;
        IsViewerClosing = false;
        ViewerShot = null;
        _viewerIndex = 0;

        Dispatcher.UIThread.Post(() => IsOpen = true, DispatcherPriority.Loaded);

        Gallery.Clear();
        Versions.Clear();
        Pages.Clear();
        _allVersions.Clear();

        IsVersionsTab = false;
        Page = 0;
        Sort = VersionSort.Released;
        IsDescending = true;

        Refresh();

        _ = LoadAsync(mod, request.Token);
    }

    [RelayCommand]
    private void OpenViewer(GalleryShot? shot)
    {
        if (shot is null) return;

        _viewerIndex = Math.Max(0, Gallery.IndexOf(shot));
        ShowShot();

        IsViewerClosing = false;
        IsViewerOpen = true;
    }

    [RelayCommand]
    private void NextShot() => MoveShot(1);

    [RelayCommand]
    private void PreviousShot() => MoveShot(-1);

    /// <summary>Wraps, because the end of a gallery is not a wall.</summary>
    private void MoveShot(int delta)
    {
        if (Gallery.Count == 0) return;

        _viewerIndex = (_viewerIndex + delta + Gallery.Count) % Gallery.Count;
        ShowShot();
    }

    private void ShowShot()
    {
        ViewerShot = Gallery[_viewerIndex];

        OnPropertyChanged(nameof(ViewerCounter));
        OnPropertyChanged(nameof(HasManyShots));

        if (_load is { } request) _ = LoadLargeAsync(ViewerShot, request.Token);
    }

    /// <summary>
    /// Fades away rather than cutting. Stays mounted for the length of it — dropping it first
    /// would take the picture off screen before a frame had drawn.
    /// </summary>
    [RelayCommand]
    private async Task CloseViewerAsync()
    {
        if (IsViewerClosing) return;

        IsViewerClosing = true;
        await Task.Delay(ViewerMilliseconds);

        IsViewerOpen = false;
        IsViewerClosing = false;
    }

    private Task LoadLargeAsync(GalleryShot shot, CancellationToken cancellationToken) =>
        shot.Large is not null || shot.LargeFrames is not null
            ? Task.CompletedTask
            : LoadArtworkAsync(
                shot.Url,
                ViewerWidth,
                AnimatedFrames.ViewerWidth,
                cancellationToken,
                picture => shot.Large = picture,
                frames => shot.LargeFrames = frames);

    /// <summary>
    /// One fetch, then either frames or a still. Anything that turns out not to move — and
    /// anything too big to be worth holding every frame of — takes the still path, which is
    /// what every picture did before animations were understood at all.
    /// </summary>
    private async Task LoadArtworkAsync(
        string url,
        int width,
        int animationLimit,
        CancellationToken cancellationToken,
        Action<Bitmap> still,
        Action<AnimatedFrames> moving)
    {
        if (url.Length == 0) return;

        try
        {
            var bytes = await launcher.Web.GetAsync(url, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

            if (AnimatedFrames.Decode(bytes, animationLimit) is { } frames)
            {
                await Dispatcher.UIThread.InvokeAsync(() => moving(frames));
                return;
            }

            using var stream = new System.IO.MemoryStream(bytes);
            var picture = Bitmap.DecodeToWidth(stream, width);

            await Dispatcher.UIThread.InvokeAsync(() => still(picture));
        }
        catch (Exception)
        {
        }
    }

    [RelayCommand]
    private void ShowOverview() => IsVersionsTab = false;

    [RelayCommand]
    private void ShowVersions()
    {
        IsVersionsTab = true;
        _ = LoadVersionsAsync();
    }

    partial void OnIsVersionsTabChanged(bool value) => OnPropertyChanged(nameof(IsOverviewTab));

    /// <summary>Matches the slide in Asobu.axaml; keep the two in step.</summary>
    private const int SlideMilliseconds = 340;

    /// <summary>
    /// Slides away rather than cutting. The page stays mounted for the length of the slide —
    /// dropping it first would take the whole thing off screen before a frame had drawn.
    /// </summary>
    [RelayCommand]
    private async Task GoBackAsync()
    {
        if (IsClosing) return;

        IsClosing = true;
        await Task.Delay(SlideMilliseconds);

        _back?.Invoke();
        IsClosing = false;
    }

    [RelayCommand]
    private void OpenOnline()
    {
        if (Card is { } card) AsobuLauncher.OpenUrl(card.Mod.PageUrl);
    }

    /// <summary>Installs whatever the provider considers current — the big button.</summary>
    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (Card is not { } card) return;

        if (card.Mod.IsPack)
        {
            askCreatePack(card.Mod, null);
            return;
        }

        if (Target is { } target)
        {
            await ModInstall.RunAsync(launcher, target, card);
            Refresh();
            return;
        }

        askInstall(
            $"Install {card.Title}",
            async instance =>
            {
                var notice = await ModInstall.RunAsync(launcher, instance, card);
                Refresh();

                return notice;
            },
            async token => ModSupport.From(await launcher.Mods.GetVersionsAsync(card.Mod, token)));
    }

    /// <summary>
    /// The builds the target instance could actually run, or all of them when there is no
    /// target.
    ///
    /// Only a build that names an actual mod loader is held to one: Modrinth files resource
    /// packs under a "minecraft" loader and shaders under "iris" and "optifine", and reading
    /// those as loader requirements would empty the table for everything that is not a mod.
    /// </summary>
    private IEnumerable<ModVersion> Runnable(IReadOnlyList<ModVersion> versions)
    {
        if (Target is not { } instance) return versions;

        return versions.Where(version =>
            (version.GameVersions.Count == 0
             || version.GameVersions.Contains(instance.MinecraftVersion, StringComparer.OrdinalIgnoreCase))
            && RunsOn(version.Loaders, instance.Loader));
    }

    private static bool RunsOn(IReadOnlyList<string> declared, string loader)
    {
        var required = declared.Where(Loaders.IsLoaderName).ToList();

        return required.Count == 0
               || required.Any(name => name.Equals(loader, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Fetched the first time the tab is opened, not with the page: most visits never
    /// look at it, and a mod with three hundred builds is a request worth not making.</summary>
    private async Task LoadVersionsAsync()
    {
        if (_allVersions.Count > 0 || IsLoadingVersions || Card is not { } card) return;

        var request = _load;
        if (request is null) return;

        IsLoadingVersions = true;

        try
        {
            var versions = await launcher.Mods.GetVersionsAsync(card.Mod, request.Token);
            if (request.IsCancellationRequested) return;

            foreach (var version in Runnable(versions)) _allVersions.Add(new ModVersionRow(version));

            Page = 0;
            Rebuild();
        }
        catch (Exception)
        {
            // An empty table says the same thing to the reader as an error would.
        }
        finally
        {
            IsLoadingVersions = false;
        }
    }

    /// <summary>
    /// Clicking the column already sorted by turns it around; clicking another switches to it,
    /// starting at the useful end — newest, highest, most finished.
    /// </summary>
    [RelayCommand]
    private void SortBy(string? column)
    {
        var wanted = column switch
        {
            "game" => VersionSort.GameVersion,
            "type" => VersionSort.Channel,
            _ => VersionSort.Released,
        };

        if (Sort == wanted) IsDescending = !IsDescending;
        else
        {
            Sort = wanted;
            IsDescending = true;
        }

        // A reordered table has no business staying on page seven of the old order.
        Page = 0;
        Rebuild();
    }

    [RelayCommand]
    private void NextPage()
    {
        if (!CanGoForwardAPage) return;

        Page++;
        Rebuild();
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (!CanGoBackAPage) return;

        Page--;
        Rebuild();
    }

    [RelayCommand]
    private void GoToPage(PageNumber? page)
    {
        if (page is null) return;

        Page = Math.Clamp(page.Number - 1, 0, PageCount - 1);
        Rebuild();
    }

    /// <summary>Sorts everything, then hands the current page to the table.</summary>
    private void Rebuild()
    {
        var sorted = Sort switch
        {
            VersionSort.GameVersion => _allVersions
                .OrderBy(row => row.Version, Comparer<ModVersion>.Create(ModVersion.CompareGameVersions)),
            VersionSort.Channel => _allVersions.OrderBy(row => row.Version.Channel),
            _ => _allVersions.OrderBy(row => row.Version.Published ?? DateTimeOffset.MinValue),
        };

        // Descending is the default for every column, so it is applied here rather than needing a
        // mirrored OrderByDescending of each.
        var ordered = IsDescending ? sorted.Reverse() : sorted;

        Page = Math.Clamp(Page, 0, PageCount - 1);

        Versions.Clear();
        foreach (var row in ordered.Skip(Page * PageSize).Take(PageSize)) Versions.Add(row);

        RebuildPages();

        OnPropertyChanged(nameof(HasVersions));
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(HasPages));
        OnPropertyChanged(nameof(CanGoBackAPage));
        OnPropertyChanged(nameof(CanGoForwardAPage));
        OnPropertyChanged(nameof(ReleasedArrow));
        OnPropertyChanged(nameof(GameVersionArrow));
        OnPropertyChanged(nameof(ChannelArrow));
    }

    /// <summary>
    /// The strip of page numbers. A window rather than all of them: some mods have published
    /// three hundred builds, and fifteen numbered buttons is a paragraph, not a control.
    /// </summary>
    private void RebuildPages()
    {
        const int Window = 7;

        var first = Math.Max(0, Math.Min(Page - Window / 2, PageCount - Window));
        var last = Math.Min(PageCount - 1, first + Window - 1);

        Pages.Clear();
        for (var i = Math.Max(0, first); i <= last; i++) Pages.Add(new PageNumber(i + 1, i == Page));
    }

    /// <summary>Installs one particular build, rather than whichever is current.</summary>
    [RelayCommand]
    private async Task DownloadVersionAsync(ModVersionRow? row)
    {
        if (row is null || row.IsInstalling) return;

        if (Card is { Mod.IsPack: true } pack)
        {
            askCreatePack(pack.Mod, row.Version);
            return;
        }

        if (Target is { } target)
        {
            await InstallVersionAsync(target, row);
            return;
        }

        // One build rather than all of them: picking a version out of the table means that
        // version, so only the instances it was published for can take it.
        askInstall(
            $"Install {row.Name}",
            instance => InstallVersionAsync(instance, row),
            _ => Task.FromResult(ModSupport.From([row.Version])));
    }

    private async Task<string?> InstallVersionAsync(Instance instance, ModVersionRow row)
    {
        row.IsInstalling = true;
        row.Notice = null;

        try
        {
            var result = await launcher.InstallVersionAsync(
                instance, row.Version, Card?.Mod.Kind ?? ModKind.Mod);

            if (result.Installed)
            {
                row.IsInstalled = true;

                row.Notice = result.Dependencies.Count == 0
                    ? $"Added {result.FileName} to {instance.Name}"
                    : $"Added {result.FileName} to {instance.Name}, with {result.Dependencies.Count} " +
                      (result.Dependencies.Count == 1 ? "dependency" : "dependencies");

                return null;
            }

            row.Notice = result.Reason ?? "The author allows downloads from their page only.";
        }
        catch (Exception ex)
        {
            row.Notice = ex.Message;
        }
        finally
        {
            row.IsInstalling = false;
        }

        return row.Notice;
    }

    private async Task LoadAsync(CatalogueMod mod, CancellationToken cancellationToken)
    {
        IsLoading = true;

        try
        {
            _ = LoadIconAsync(cancellationToken);
            _ = LoadBannerAsync(mod, cancellationToken);

            var details = await launcher.Mods.GetDetailsAsync(mod, cancellationToken);
            if (cancellationToken.IsCancellationRequested || details is null) return;

            foreach (var block in details.Description) Description.Add(block);

            OnPropertyChanged(nameof(HasDescription));

            for (var i = 0; i < details.Gallery.Count && Gallery.Count < 6; i++)
            {
                var image = details.Gallery[i];
                var shot = new GalleryShot(image) { IsBannerSource = IsBannerSource(mod, image, i) };

                // Named plainly enough that there is no need to look at it first.
                if (shot.IsBannerSource && shot.NamedAsBanner) continue;

                Gallery.Add(shot);
                _ = LoadShotAsync(shot, cancellationToken);
            }

            OnPropertyChanged(nameof(HasGallery));
        }
        catch (Exception)
        {
            // A page with only what the search result carried is still a page.
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) IsLoading = false;
        }
    }

    private async Task LoadIconAsync(CancellationToken cancellationToken)
    {
        if (Card is not { } card || card.Mod.IconUrl is not { Length: > 0 } url) return;

        try
        {
            var bytes = await launcher.Web.GetAsync(url, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

            var icon = ImageCache.FromBytes(url, bytes);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                card.Icon = icon;
                OnPropertyChanged(nameof(Icon));
                OnPropertyChanged(nameof(HasIcon));
            });
        }
        catch (Exception)
        {
        }
    }

    private async Task LoadBannerAsync(CatalogueMod mod, CancellationToken cancellationToken)
    {
        // Scenery until the mod's own picture arrives, and instead of it where there is none.
        // Picked from the title, so a given mod always gets the same one.
        Banner = Backdrops.Any(mod.Title);
        OnPropertyChanged(nameof(HasBanner));

        if (mod.GalleryUrl is not { Length: > 0 } url) return;

        await LoadPictureAsync(url, BannerWidth, cancellationToken, picture =>
        {
            Banner = picture;
            OnPropertyChanged(nameof(HasBanner));
        });
    }

    /// <summary>
    /// Whether this is the picture the banner is using. Matched by address where the two agree,
    /// and otherwise by position — the banner takes the featured picture, which is the one the
    /// gallery lists first.
    /// </summary>
    private static bool IsBannerSource(CatalogueMod mod, ModImage image, int index) =>
        mod.GalleryUrl is { Length: > 0 } banner
            ? string.Equals(banner, image.Url, StringComparison.OrdinalIgnoreCase)
              || string.Equals(banner, image.ThumbUrl, StringComparison.OrdinalIgnoreCase)
              || index == 0
            : false;

    /// <summary>
    /// The column's copy. Fetches the provider's own thumbnail rather than the full upload:
    /// it is the right size for a 320px column, and small enough that an animated one can be
    /// played rather than frozen.
    /// </summary>
    private Task LoadShotAsync(GalleryShot shot, CancellationToken cancellationToken) =>
        LoadArtworkAsync(
            shot.ThumbUrl,
            ShotWidth,
            AnimatedFrames.ThumbnailWidth,
            cancellationToken,
            picture =>
            {
                shot.Picture = picture;
                DropIfBannerPlate(shot);
            },
            frames => shot.Frames = frames);

    /// <summary>
    /// Takes the banner's own picture out of the gallery once it can be seen to be a banner
    /// plate. Removed rather than hidden: the viewer pages through this list and counts it, and
    /// an entry that is there but never shown would make both wrong.
    /// </summary>
    private void DropIfBannerPlate(GalleryShot shot)
    {
        if (!shot.IsBannerSource || !shot.ShapedAsBanner) return;

        Gallery.Remove(shot);

        if (ReferenceEquals(ViewerShot, shot)) _ = CloseViewerAsync();

        _viewerIndex = Math.Clamp(_viewerIndex, 0, Math.Max(0, Gallery.Count - 1));

        OnPropertyChanged(nameof(HasGallery));
        OnPropertyChanged(nameof(ViewerCounter));
        OnPropertyChanged(nameof(HasManyShots));
    }

    /// <summary>
    /// Scaled down on the way in and kept out of the shared image cache: gallery pictures are
    /// published at full screenshot size, and a page of them at native resolution is tens of
    /// megabytes to show a column three hundred pixels wide.
    /// </summary>
    private async Task LoadPictureAsync(
        string url, int width, CancellationToken cancellationToken, Action<Bitmap> apply)
    {
        try
        {
            var bytes = await launcher.Web.GetAsync(url, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

            using var stream = new System.IO.MemoryStream(bytes);
            var picture = Bitmap.DecodeToWidth(stream, width);

            await Dispatcher.UIThread.InvokeAsync(() => apply(picture));
        }
        catch (Exception)
        {
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(AuthorLabel));
        OnPropertyChanged(nameof(HasAuthor));
        OnPropertyChanged(nameof(MetaLine));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(HasIcon));
        OnPropertyChanged(nameof(HasBanner));
        OnPropertyChanged(nameof(HasGallery));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(HasVersions));
        OnPropertyChanged(nameof(IsOverviewTab));
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(HasPages));
    }
}
