using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Asobu.Core;
using Asobu.Core.Accounts;
using Asobu.Core.Skins;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;

namespace Asobu.App.ViewModels;

public enum SkinsTab
{
    Mine,
    Browse,
    Draw,
}

/// <summary>What the pencil does when it lands on a pixel.</summary>
public enum SkinTool
{
    Pencil,
    Eraser,
    Fill,
    Picker,
}

/// <summary>One skin on the public gallery, with the figure drawn for its card.</summary>
public partial class GalleryCard(GallerySkin skin) : ViewModelBase
{
    public GallerySkin Skin { get; } = skin;

    /// <summary>Worked out from the file, since the listing does not say.</summary>
    [ObservableProperty] public partial SkinModel Model { get; set; } = SkinModel.Classic;

    [ObservableProperty] public partial Bitmap? Thumbnail { get; set; }

    public string Label => Skin.Name is { Length: > 0 } named ? named : ModelLabel;

    public string ModelLabel => Model == SkinModel.Slim ? "Slim" : "Classic";

    partial void OnModelChanged(SkinModel value)
    {
        OnPropertyChanged(nameof(ModelLabel));
        OnPropertyChanged(nameof(Label));
    }
}

/// <summary>One skin in the library, with the little figure shown on its card.</summary>
public partial class SkinCard(SavedSkin saved) : ViewModelBase
{
    public SavedSkin Saved { get; private set; } = saved;

    /// <summary>
    /// What removing this card does. Held on the card because the menu that offers it lives in a
    /// popup, and a popup is its own little tree — it cannot see up into the page the way an
    /// ordinary child can.
    /// </summary>
    public Action<SkinCard>? OnRemove { get; init; }

    [RelayCommand]
    private void Remove() => OnRemove?.Invoke(this);

    public string Name => Saved.Name;
    public string ModelLabel => Saved.Model == SkinModel.Slim ? "Slim" : "Classic";

    [ObservableProperty] public partial Bitmap? Thumbnail { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }

    public void Renamed(SavedSkin saved)
    {
        Saved = saved;
        OnPropertyChanged(nameof(Name));
    }
}

/// <summary>
/// Skins: the ones you have kept, anybody else's, and a place to draw your own.
///
/// The three share one thing — the figure on the left — so the skin being looked at lives here
/// rather than in each tab, and every tab is really just a different way of choosing it.
/// </summary>
public partial class SkinsViewModel : ViewModelBase
{
    private readonly AsobuLauncher _launcher;
    private readonly AccountsViewModel _accounts;
    private readonly SkinLibrary _library;
    private readonly SkinService _service;

    private CancellationTokenSource? _searchCts;

    public SkinsViewModel(AsobuLauncher launcher, AccountsViewModel accounts)
    {
        _launcher = launcher;
        _accounts = accounts;
        _library = new SkinLibrary(launcher.Paths);
        _service = new SkinService(launcher.Http);

        Editor = new uint[64 * 64];
        NewDrawing();
    }

    // ---- Which tab ----

    [ObservableProperty] public partial SkinsTab Tab { get; set; } = SkinsTab.Mine;

    partial void OnTabChanged(SkinsTab value)
    {
        OnPropertyChanged(nameof(IsMine));
        OnPropertyChanged(nameof(IsBrowse));
        OnPropertyChanged(nameof(IsDraw));

        // The drawing is its own skin, and it belongs to its own tab. Stepping in shows it;
        // stepping out puts back whatever was on the stage before, which otherwise stayed
        // replaced by a half-finished drawing for the rest of the session.
        if (value == SkinsTab.Draw)
        {
            _beforeDrawing = _shown is null ? null : (_shown, ShownModel);
            ShowDrawing();
        }
        else if (_beforeDrawing is { } previous)
        {
            Show(previous.Png, previous.Model);
            _beforeDrawing = null;
        }

        // Fetched the first time somebody looks, not on startup: it is somebody else's web page,
        // and a launcher that reached for it at every launch would be rude about it.
        if (value == SkinsTab.Browse && Gallery.Count == 0) _ = LoadGalleryAsync();
    }

    public bool IsMine => Tab == SkinsTab.Mine;
    public bool IsBrowse => Tab == SkinsTab.Browse;
    public bool IsDraw => Tab == SkinsTab.Draw;

    [RelayCommand] private void GoMine() => Tab = SkinsTab.Mine;
    [RelayCommand] private void GoBrowse() => Tab = SkinsTab.Browse;
    [RelayCommand] private void GoDraw() => Tab = SkinsTab.Draw;

    // ---- The figure ----

    /// <summary>The skin the figure is wearing, as the PNG it came from.</summary>
    private byte[]? _shown;

    /// <summary>What was on the stage before the editor took it over.</summary>
    private (byte[] Png, SkinModel Model)? _beforeDrawing;

    [ObservableProperty] public partial SkinModel ShownModel { get; set; } = SkinModel.Classic;
    [ObservableProperty] public partial Bitmap? Figure { get; set; }
    [ObservableProperty] public partial string? Status { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    /// <summary>Turned by dragging. Starts a little off square so it reads as a solid rather than a picture.</summary>
    [ObservableProperty] public partial double Yaw { get; set; } = -0.5;
    [ObservableProperty] public partial double Pitch { get; set; } = -0.12;

    [ObservableProperty] public partial bool ShowOuterLayer { get; set; } = true;

    // Which pieces are on screen. Drawing a boot is easier without a leg of trouser over it.
    [ObservableProperty] public partial bool ShowHead { get; set; } = true;
    [ObservableProperty] public partial bool ShowBody { get; set; } = true;
    [ObservableProperty] public partial bool ShowArms { get; set; } = true;
    [ObservableProperty] public partial bool ShowLegs { get; set; } = true;

    partial void OnShowHeadChanged(bool value) => Redraw();
    partial void OnShowBodyChanged(bool value) => Redraw();
    partial void OnShowArmsChanged(bool value) => Redraw();
    partial void OnShowLegsChanged(bool value) => Redraw();

    private SkinParts Parts =>
        (ShowHead ? SkinParts.Head : 0) | (ShowBody ? SkinParts.Body : 0)
        | (ShowArms ? SkinParts.Arms : 0) | (ShowLegs ? SkinParts.Legs : 0);

    /// <summary>How big the figure is drawn, and so how big its pick map is.</summary>
    private const int FigureWidth = 260;
    private const int FigureHeight = 340;

    /// <summary>
    /// Which texture pixel sits under each pixel of the figure.
    ///
    /// Worked out only when somebody actually draws on the model, and thrown away whenever it
    /// turns: it is a second full render, and paying for one on every frame of a drag to answer a
    /// question nobody asked would make turning the figure crawl.
    /// </summary>
    private int[]? _pick;

    partial void OnYawChanged(double value) => Redraw();
    partial void OnPitchChanged(double value) => Redraw();
    partial void OnShowOuterLayerChanged(bool value) => Redraw();
    partial void OnShownModelChanged(SkinModel value) => Redraw();

    /// <summary>Dragging the figure turns it. Pitch is held short of straight up or down.</summary>
    public void Drag(double dx, double dy)
    {
        // Away from the drag, not with it. The hand is on the near side of the figure, so pulling
        // right has to carry the near side right — which turns the model the other way.
        Yaw -= dx * 0.01;
        Pitch = Math.Clamp(Pitch + dy * 0.01, -0.9, 0.9);
    }

    [RelayCommand]
    private void FaceForward()
    {
        Yaw = -0.5;
        Pitch = -0.12;
    }

    private void Redraw()
    {
        _pick = null;

        Figure = _shown is null
            ? null
            : SkinRenderer.Render(
                _shown, ShownModel, Yaw, Pitch, FigureWidth, FigureHeight, ShowOuterLayer, Parts);
    }

    /// <summary>
    /// Draws on the figure itself. The point is in the rendered figure's own pixels; the view
    /// works that out from where the picture ended up on screen.
    /// </summary>
    public void PaintOnFigure(double fx, double fy)
    {
        if (!IsDraw || _shown is null) return;

        _pick ??= SkinRenderer.PickMap(
            _shown, ShownModel, Yaw, Pitch, FigureWidth, FigureHeight, DrawingOuter, Parts);

        int x = (int)fx, y = (int)fy;
        if (x < 0 || y < 0 || x >= FigureWidth || y >= FigureHeight) return;

        var texel = _pick[y * FigureWidth + x];
        if (texel < 0) return;

        Paint(texel % 64, texel / 64);
    }

    private void Show(byte[] png, SkinModel model)
    {
        _shown = png;
        ShownModel = model;
        Redraw();
    }

    // ---- My skins ----

    public ObservableCollection<SkinCard> Mine { get; } = [];

    public bool HasNoSkins => Mine.Count == 0;

    [ObservableProperty] public partial SkinCard? Selected { get; set; }

    partial void OnSelectedChanged(SkinCard? value)
    {
        foreach (var card in Mine) card.IsSelected = ReferenceEquals(card, value);

        if (value is null) return;

        try
        {
            Show(File.ReadAllBytes(value.Saved.Path), value.Saved.Model);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The file went while the card was still on screen — deleted from the folder, or
            // lost to a half-finished rename. Not worth a red line: drop the card and move on.
            Mine.Remove(value);
            OnPropertyChanged(nameof(HasNoSkins));

            Selected = Mine.FirstOrDefault();
        }
    }

    /// <summary>Every card is built here, so every card can be removed from its own menu.</summary>
    private SkinCard Card(SavedSkin saved) => new(saved) { OnRemove = Delete };

    public void Reload()
    {
        Mine.Clear();
        foreach (var saved in _library.All()) Mine.Add(Card(saved));

        OnPropertyChanged(nameof(HasNoSkins));

        _ = DrawThumbnailsAsync();

        // Their own skin, every time. This used to be reached only when the stage was empty,
        // which it never is — the editor fills it with a starting drawing before anybody has
        // opened the page — so the one skin a person certainly wants to see was the one skin
        // that never appeared.
        _ = ShowCurrentSkinAsync();
    }

    /// <summary>
    /// The cards' little figures. Off the UI thread because each is a full render, and a library
    /// of twenty would otherwise be twenty renders between one frame and the next.
    /// </summary>
    private async Task DrawThumbnailsAsync()
    {
        foreach (var card in Mine.ToList())
        {
            if (card.Thumbnail is not null) continue;

            var saved = card.Saved;

            var drawn = await Task.Run(() =>
            {
                try
                {
                    var png = File.ReadAllBytes(saved.Path);
                    using var skin = SKBitmap.Decode(png);

                    return skin is null ? null : SkinRenderer.Pixels(skin, saved.Model, -0.5, -0.12, 96, 126);
                }
                catch (Exception e) when (e is IOException or ArgumentException)
                {
                    return null;
                }
            });

            if (drawn is not null) card.Thumbnail = ToBitmap(drawn, 96, 126);
        }
    }

    /// <summary>
    /// The skin the signed-in account is wearing right now, on the stage and in the library.
    ///
    /// Kept as well as shown, because it is the one skin somebody is certain to want back: the
    /// moment they try another one, theirs is gone from Mojang and the only copy left is this.
    /// </summary>
    private async Task ShowCurrentSkinAsync()
    {
        if (_accounts.Active is not { } account) return;

        try
        {
            var worn = await _service.OfUuidAsync(account.Uuid, account.Username);
            if (worn is null) return;

            var png = await _service.DownloadAsync(worn.Url);

            // Not over the top of the editor: somebody in the middle of drawing did not ask for
            // their old skin back.
            if (!IsDraw) Show(png, worn.Model);

            Remember(png, worn.Model, account.Username);
        }
        catch (Exception e) when (e is HttpRequestException or SkinException or TaskCanceledException)
        {
            // Offline, or an account with no Mojang profile. Nothing to add and nothing to say.
        }
    }

    /// <summary>Files the account's own skin, unless the library already has that exact image.</summary>
    private void Remember(byte[] png, SkinModel model, string username)
    {
        foreach (var card in Mine)
        {
            try
            {
                if (File.ReadAllBytes(card.Saved.Path).AsSpan().SequenceEqual(png))
                {
                    Selected ??= card;
                    return;
                }
            }
            catch (IOException)
            {
            }
        }

        try
        {
            var card = Card(_library.Save(png, username, model));

            Mine.Insert(0, card);
            OnPropertyChanged(nameof(HasNoSkins));

            Selected ??= card;
            _ = DrawThumbnailsAsync();
        }
        catch (Exception e) when (e is SkinException or IOException)
        {
            Error = e.Message;
        }
    }

    /// <summary>Called by the view once its file picker has an answer.</summary>
    public void ImportFromFile(string path)
    {
        Error = null;

        try
        {
            var png = File.ReadAllBytes(path);
            SkinPng.Validate(png);

            // Guessed from the file, and changeable on the card afterwards: nothing in a PNG says
            // which arms it was drawn for, and asking before they have even seen it is worse than
            // guessing the common one.
            var saved = _library.Save(png, Path.GetFileNameWithoutExtension(path), GuessModel(png));
            var card = Card(saved);

            Mine.Insert(0, card);
            OnPropertyChanged(nameof(HasNoSkins));

            Selected = card;
            Status = $"Added {saved.Name}.";
            _ = DrawThumbnailsAsync();
        }
        catch (Exception e) when (e is SkinException or IOException)
        {
            Error = e.Message;
        }
    }

    /// <summary>
    /// Whether a skin looks like it was drawn for slim arms.
    ///
    /// A slim skin only uses three of the four pixels its arm is given, so the fourth column is
    /// left transparent. That is a guess rather than a rule, and it is one the person can correct
    /// on the card — but it is right for every slim skin that was exported properly.
    /// </summary>
    private static SkinModel GuessModel(byte[] png)
    {
        try
        {
            using var skin = SKBitmap.Decode(png);
            if (skin is null || skin.Height < 64) return SkinModel.Classic;

            // The right arm's front face starts at (44,20); its fourth column is x=47.
            for (var y = 20; y < 32; y++)
                if (skin.GetPixel(47, y).Alpha != 0)
                    return SkinModel.Classic;

            return SkinModel.Slim;
        }
        catch (ArgumentException)
        {
            return SkinModel.Classic;
        }
    }

    [RelayCommand]
    private void ToggleModel()
    {
        if (Selected is not { } card) return;

        var model = card.Saved.Model == SkinModel.Slim ? SkinModel.Classic : SkinModel.Slim;

        try
        {
            // Read and rewrite before removing the old one. Removing first deleted the file this
            // very line needs, and left a card pointing at nothing.
            var png = File.ReadAllBytes(card.Saved.Path);
            var saved = _library.Save(png, card.Saved.Name, model);
            var replacement = Card(saved);

            var at = Mine.IndexOf(card);
            Mine[at] = replacement;

            _library.Remove(card.Saved);

            Selected = replacement;
            _ = DrawThumbnailsAsync();
        }
        catch (Exception e) when (e is SkinException or IOException)
        {
            Error = e.Message;
        }
    }

    [RelayCommand]
    private void Delete(SkinCard? card)
    {
        if (card is null) return;

        _library.Remove(card.Saved);
        Mine.Remove(card);
        OnPropertyChanged(nameof(HasNoSkins));

        if (ReferenceEquals(Selected, card)) Selected = Mine.FirstOrDefault();
    }

    /// <summary>
    /// Whether wearing a skin is possible at all right now.
    ///
    /// Asked before the button is pressed rather than after. An offline account has no Mojang
    /// profile to change, and finding that out by clicking a button that looked available is a
    /// worse way to learn it than the button simply not being available.
    /// </summary>
    /// <summary>
    /// Wearing works either way now — through Mojang for an account it knows, and through the
    /// instances themselves for one it does not.
    /// </summary>
    public bool CanWear => !IsBusy && _accounts.Active is not null;

    /// <summary>An upload in flight is also a reason the button cannot be pressed again.</summary>
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanWear));

    public string WearNote => _accounts.Active switch
    {
        null => "Sign in to an account to wear a skin.",
        { Kind: AccountKind.Microsoft } account =>
            $"Puts it on {account.Username}'s Microsoft account, and into your instances as a "
            + "fallback for when the game can't reach it.",

        // Offline accounts get it locally instead: the game has no skin to fetch for them, so
        // the one it falls back to is replaced in every instance.
        _ => "Offline accounts get it locally — Asobu puts it in your instances as a resource "
             + "pack, so you see it yourself. Other players won't.",
    };

    /// <summary>What the local half of wearing actually does, said where it can be read beforehand.</summary>
    public string LocalNote =>
        "The local copy is an ordinary resource pack named asobu-skin. It only shows where the "
        + "game would otherwise draw the default player, so anyone else whose skin fails to load "
        + "appears wearing it too. Turn it off in the game's resource pack list to stop that.";

    /// <summary>Puts what the figure is wearing onto the signed-in account.</summary>
    [RelayCommand]
    private async Task WearAsync()
    {
        if (_shown is not { } png) return;

        Error = null;
        Status = null;

        if (_accounts.Active is not { } account)
        {
            Error = "Sign in to an account first.";
            return;
        }

        IsBusy = true;

        // Locally either way, and first.
        //
        // For an offline account it is the only thing that can work. For a Microsoft one it is
        // the safety net: the game only draws the skin Mojang serves if it manages to resolve the
        // profile, and there are several ways for that to fail that have nothing to do with the
        // skin being right — a server that hands out unsigned profiles, a mod in the middle, no
        // connection at the moment it asked. When it fails the game falls back to the default
        // player, and this makes the default player you.
        var locally = WearLocally(png);

        if (account.Kind != AccountKind.Microsoft)
        {
            Status = locally;
            IsBusy = false;
            return;
        }

        try
        {
            var session = await _launcher.ResolveSessionAsync(account);
            await _service.ApplyAsync(session, png, ShownModel);

            // Asked back rather than assumed. Mojang took the upload, but "it took it" and "it is
            // wearing it" are different claims, and the second is the one worth making — so the
            // profile is read again and the answer compared with what was sent.
            Status = $"{account.Username} is wearing it now. {locally}";

            await Task.Delay(TimeSpan.FromSeconds(2));

            if (await _service.OfUuidAsync(account.Uuid, account.Username) is { } worn)
            {
                var live = await _service.DownloadAsync(worn.Url);

                Status = live.AsSpan().SequenceEqual(png)
                    ? $"{account.Username} is wearing it — Mojang confirms it. {locally}"
                    : $"Mojang took the skin but is still serving the old one; it usually catches up within a minute. {locally}";
            }
        }
        catch (Exception e) when (e is SkinException or MicrosoftAuthException or HttpRequestException)
        {
            Error = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Wears a skin without Mojang, by putting it in the instances as the default player.
    ///
    /// Every instance rather than one, because the skin belongs to the person rather than to a
    /// world — and because being asked which instance you would like to look like yourself in is
    /// a strange question.
    /// </summary>
    private string? WearLocally(byte[] png)
    {
        var instances = _launcher.Instances.LoadAll();

        if (instances.Count == 0) return null;

        var done = 0;
        string? failed = null;

        foreach (var instance in instances)
        {
            try
            {
                var gameDir = _launcher.Paths.InstanceGameDir(instance.Folder);

                SkinPack.Write(gameDir, png, instance.MinecraftVersion);
                SkinPack.Enable(gameDir);

                done++;
            }
            catch (Exception e) when (e is IOException or SkinException or UnauthorizedAccessException)
            {
                failed ??= e.Message;
            }
        }

        if (done == 0)
        {
            Error = failed;
            return null;
        }

        return $"Wearing it in {done} instance{(done == 1 ? "" : "s")} — it shows next time each one starts.";
    }

    // ---- Browse ----

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial PlayerSkin? Found { get; set; }
    [ObservableProperty] public partial bool IsSearching { get; set; }
    [ObservableProperty] public partial string? SearchNote { get; set; }
    [ObservableProperty] public partial bool IsLoadingGallery { get; set; }

    /// <summary>Skins to look at without having to know a name first.</summary>
    public ObservableCollection<GalleryCard> Gallery { get; } = [];

    public bool HasGallery => Gallery.Count > 0;

    /// <summary>Where the next page of the gallery starts, or null once it has run out.</summary>
    private string? _galleryCursor;

    private bool _galleryStarted;

    [RelayCommand]
    private async Task LoadGalleryAsync()
    {
        if (IsLoadingGallery) return;

        _galleryStarted = true;
        IsLoadingGallery = true;

        try
        {
            var page = await _service.GalleryAsync();

            Gallery.Clear();
            foreach (var skin in page.Skins) Gallery.Add(new GalleryCard(skin));

            _galleryCursor = page.Next;
            OnPropertyChanged(nameof(HasGallery));

            SearchNote = page.Skins.Count == 0
                ? "Couldn't reach the skin gallery just now. Looking a player up still works."
                : null;

            await DrawGalleryAsync();
        }
        finally
        {
            IsLoadingGallery = false;
        }
    }

    /// <summary>
    /// The next page, when somebody scrolls to the end of this one. Called by the view rather
    /// than on a timer: the shelf grows because it was looked at, not because time passed.
    /// </summary>
    public async Task LoadMoreGalleryAsync()
    {
        if (IsLoadingGallery || !_galleryStarted || _galleryCursor is not { Length: > 0 } cursor) return;

        IsLoadingGallery = true;

        try
        {
            var page = await _service.GalleryAsync(cursor);

            foreach (var skin in page.Skins)
                if (Gallery.All(card => card.Skin.Texture != skin.Texture))
                    Gallery.Add(new GalleryCard(skin));

            // Null once the gallery has no more to give, which stops the scroll asking again.
            _galleryCursor = page.Next == cursor ? null : page.Next;

            await DrawGalleryAsync();
        }
        finally
        {
            IsLoadingGallery = false;
        }
    }

    /// <summary>
    /// Each gallery card's figure, drawn from the real texture rather than shown as somebody
    /// else's picture — so the cards here look exactly like the cards under My Skins.
    /// </summary>
    private async Task DrawGalleryAsync()
    {
        foreach (var card in Gallery.ToList())
        {
            if (card.Thumbnail is not null) continue;

            try
            {
                var png = await _service.TextureAsync(card.Skin.Texture);

                // Most of what gets uploaded to a public skin API is not a skin. Blank sheets,
                // half-finished tests, one pixel in a corner — a shelf of those is worse than a
                // shorter shelf, so anything that is barely drawn on never becomes a card.
                if (!LooksDrawn(png))
                {
                    Gallery.Remove(card);
                    continue;
                }

                // The listing says nothing about arms, so the file is asked instead — the same
                // guess an imported PNG gets.
                var model = GuessModel(png);
                card.Model = model;

                var drawn = await Task.Run(() =>
                {
                    using var skin = SKBitmap.Decode(png);
                    return skin is null ? null : SkinRenderer.Pixels(skin, model, -0.5, -0.12, 96, 126);
                });

                if (drawn is not null) card.Thumbnail = ToBitmap(drawn, 96, 126);
            }
            catch (Exception e) when (e is HttpRequestException or ArgumentException or TaskCanceledException)
            {
                // One card that would not load. The rest of the shelf is still worth showing.
            }
        }
    }

    /// <summary>
    /// Whether a skin has actually been drawn.
    ///
    /// Judged on the front of the head and body, because that is the part no real skin leaves
    /// out: something with a face and a chest is somebody's work, and something without either
    /// is a blank sheet that happened to get uploaded.
    /// </summary>
    private static bool LooksDrawn(byte[] png)
    {
        try
        {
            using var skin = SKBitmap.Decode(png);
            if (skin is null || skin.Width < 64) return false;

            var drawn = 0;

            // The face, at (8,8), and the chest at (20,20).
            for (var y = 8; y < 16; y++)
            for (var x = 8; x < 16; x++)
                if (skin.GetPixel(x, y).Alpha > 128) drawn++;

            for (var y = 20; y < 32; y++)
            for (var x = 20; x < 28; x++)
                if (skin.GetPixel(x, y).Alpha > 128) drawn++;

            // Both regions essentially filled. A real skin fills them completely; a test upload
            // leaves them empty or nearly so.
            return drawn > (64 + 96) * 3 / 4;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [RelayCommand]
    private async Task ShowGalleryAsync(GalleryCard? card)
    {
        if (card is null) return;

        Error = null;
        Found = null;

        try
        {
            Show(await _service.TextureAsync(card.Skin.Texture), card.Model);
        }
        catch (Exception e) when (e is HttpRequestException or SkinException)
        {
            Error = e.Message;
        }
    }

    public ObservableCollection<PlayerSkin> Recent { get; } = [];

    [RelayCommand]
    private async Task SearchAsync()
    {
        var name = SearchText.Trim();
        if (name.Length == 0) return;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        IsSearching = true;
        SearchNote = null;
        Error = null;

        try
        {
            var player = await _service.FindAsync(name, token);

            if (player is null)
            {
                SearchNote = $"Nobody is called {name}.";
                Found = null;
                return;
            }

            Found = player;
            Show(await _service.DownloadAsync(player.Url, token), player.Model);

            // A short trail of who has been looked at, so going back to one is a click.
            if (Recent.FirstOrDefault(p => p.Uuid == player.Uuid) is { } already) Recent.Remove(already);
            Recent.Insert(0, player);
            while (Recent.Count > 12) Recent.RemoveAt(Recent.Count - 1);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is HttpRequestException or SkinException)
        {
            Error = e.Message;
        }
        finally
        {
            if (!token.IsCancellationRequested) IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task ShowRecentAsync(PlayerSkin? player)
    {
        if (player is null) return;

        Found = player;

        try
        {
            Show(await _service.DownloadAsync(player.Url), player.Model);
        }
        catch (Exception e) when (e is HttpRequestException or SkinException)
        {
            Error = e.Message;
        }
    }

    /// <summary>Keeps whatever the figure is wearing, whoever it came from.</summary>
    [RelayCommand]
    private void KeepShown()
    {
        if (_shown is not { } png) return;

        Error = null;

        try
        {
            var name = Tab == SkinsTab.Browse && Found is { } player ? player.Username
                : Tab == SkinsTab.Browse ? "From the gallery"
                : "Drawing";
            var card = Card(_library.Save(png, name, ShownModel));

            Mine.Insert(0, card);
            OnPropertyChanged(nameof(HasNoSkins));

            Status = $"Saved to My Skins as {card.Name}.";
            _ = DrawThumbnailsAsync();
        }
        catch (Exception e) when (e is SkinException or IOException)
        {
            Error = e.Message;
        }
    }

    // ---- Draw ----

    /// <summary>The drawing, one ARGB value per texture pixel.</summary>
    public uint[] Editor { get; }

    /// <summary>
    /// Where each half of the sheet lives.
    ///
    /// A skin is two figures on one sheet — the body, and the layer of hair and clothing that
    /// sits just outside it — and they are interleaved rather than stacked, so "which layer am I
    /// drawing on" cannot be answered by looking at the pixel. It has to be answered by where.
    /// </summary>
    private static readonly (int X, int Y, int W, int H)[] BodyParts =
    [
        (0, 0, 32, 16), (16, 16, 24, 16), (40, 16, 16, 16),
        (0, 16, 16, 16), (32, 48, 16, 16), (16, 48, 16, 16),
    ];

    private static readonly (int X, int Y, int W, int H)[] OuterParts =
    [
        (32, 0, 32, 16), (16, 32, 24, 16), (40, 32, 16, 16),
        (0, 32, 16, 16), (48, 48, 16, 16), (0, 48, 16, 16),
    ];

    [ObservableProperty] public partial bool DrawingOuter { get; set; }

    partial void OnDrawingOuterChanged(bool value)
    {
        OnPropertyChanged(nameof(DrawingBody));

        // The outer layer is only worth drawing on if it can be seen while it is drawn.
        if (value) ShowOuterLayer = true;

        ShowDrawing();
    }

    public bool DrawingBody => !DrawingOuter;

    [RelayCommand] private void DrawBodyLayer() => DrawingOuter = false;
    [RelayCommand] private void DrawOuterLayer() => DrawingOuter = true;

    private static bool In((int X, int Y, int W, int H)[] parts, int x, int y) =>
        parts.Any(p => x >= p.X && y >= p.Y && x < p.X + p.W && y < p.Y + p.H);

    /// <summary>
    /// Lists rather than stacks. The cap has to drop the oldest step, which is the end a stack
    /// will not give you — and sixty 16KB snapshots is a megabyte, which is the whole reason
    /// there is a cap.
    /// </summary>
    private readonly List<uint[]> _undo = [];
    private readonly List<uint[]> _redo = [];

    private const int UndoDepth = 60;

    [ObservableProperty] public partial Bitmap? Sheet { get; set; }
    [ObservableProperty] public partial SkinTool Tool { get; set; } = SkinTool.Pencil;
    [ObservableProperty] public partial uint Colour { get; set; } = 0xFFE0709Au;
    [ObservableProperty] public partial string ColourHex { get; set; } = "#E0709A";

    public bool IsPencil => Tool == SkinTool.Pencil;
    public bool IsEraser => Tool == SkinTool.Eraser;
    public bool IsFill => Tool == SkinTool.Fill;
    public bool IsPicker => Tool == SkinTool.Picker;

    partial void OnToolChanged(SkinTool value)
    {
        OnPropertyChanged(nameof(IsPencil));
        OnPropertyChanged(nameof(IsEraser));
        OnPropertyChanged(nameof(IsFill));
        OnPropertyChanged(nameof(IsPicker));
    }

    /// <summary>The colour as a swatch can show it — always valid, whatever is half-typed in the box.</summary>
    public string ColourSwatch => $"#{Colour & 0xFFFFFF:X6}";

    partial void OnColourChanged(uint value)
    {
        OnPropertyChanged(nameof(ColourSwatch));

        var hex = $"#{value & 0xFFFFFF:X6}";
        if (!string.Equals(ColourHex, hex, StringComparison.OrdinalIgnoreCase)) ColourHex = hex;
    }

    /// <summary>
    /// Typed in rather than picked. The two follow each other, so each only writes to the other
    /// when it would actually change it — otherwise setting one bounces back and forth forever.
    /// </summary>
    partial void OnColourHexChanged(string value)
    {
        if (TryHex(value, out var rgb) && (Colour & 0xFFFFFF) != rgb) Colour = 0xFF000000u | rgb;
    }

    /// <summary>
    /// A colour someone typed. Takes it with or without the hash, and in the three-digit short
    /// form, because those are the ways people write them — half-finished text simply is not a
    /// colour yet and is left alone until it is.
    /// </summary>
    private static bool TryHex(string text, out uint rgb)
    {
        rgb = 0;
        var digits = text.Trim().TrimStart('#');

        if (digits.Length == 3)
        {
            digits = string.Concat(digits.Select(c => new string(c, 2)));
        }

        return digits.Length == 6
            && uint.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out rgb);
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    [RelayCommand] private void PickPencil() => Tool = SkinTool.Pencil;
    [RelayCommand] private void PickEraser() => Tool = SkinTool.Eraser;
    [RelayCommand] private void PickFill() => Tool = SkinTool.Fill;
    [RelayCommand] private void PickPicker() => Tool = SkinTool.Picker;

    /// <summary>
    /// A starting palette. Skin tones, hair, and the colours clothes usually end up — enough to
    /// draw something recognisable without reaching for a colour wheel first.
    /// </summary>
    public IReadOnlyList<string> Swatches { get; } =
    [
        "#FFFFFF", "#C6C6C6", "#8C8C8C", "#4A4A4A", "#1E1E1E", "#000000",
        "#FFDBAC", "#F1C27D", "#C69076", "#8D5524", "#5A3A26", "#2E1B10",
        "#E0709A", "#C2456E", "#7A2E4A", "#F2B33D", "#D98324", "#8A4B12",
        "#8FD46A", "#4E9A3E", "#2C5E28", "#6FC3DF", "#4E7BA8", "#2A4A6E",
        "#B78BD9", "#7B4FA8", "#4A2C6E", "#35405A", "#233046", "#12182A",
    ];

    [RelayCommand]
    private void Select(SkinCard? card)
    {
        if (card is not null) Selected = card;
    }

    [RelayCommand]
    private void UseSwatch(string? hex)
    {
        if (hex is not null && TryHex(hex, out var rgb)) Colour = 0xFF000000u | rgb;
    }

    /// <summary>A stroke is one undo step, however many pixels it covered.</summary>
    public void BeginStroke()
    {
        _undo.Add((uint[])Editor.Clone());
        _redo.Clear();

        while (_undo.Count > UndoDepth) _undo.RemoveAt(0);

        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    /// <summary>Called by the canvas for each texture pixel the pointer touches.</summary>
    public void Paint(int x, int y)
    {
        if (x < 0 || y < 0 || x >= 64 || y >= 64) return;

        // Off the layer being drawn on. Silently ignored rather than refused: the sheet has to
        // show both layers to be legible, so the pointer crosses the other one constantly.
        if (!In(DrawingOuter ? OuterParts : BodyParts, x, y)) return;

        var at = y * 64 + x;

        switch (Tool)
        {
            case SkinTool.Pencil:
                Editor[at] = Colour;
                break;

            case SkinTool.Eraser:
                Editor[at] = 0;
                break;

            case SkinTool.Picker:
                if (Editor[at] >> 24 != 0) Colour = Editor[at];
                return;

            case SkinTool.Fill:
                FloodFill(x, y);
                break;
        }

        ShowDrawing();
    }

    /// <summary>
    /// Spreads from one pixel over everything of the same colour joined to it. Iterative rather
    /// than recursive: a whole empty 64×64 sheet is four thousand pixels deep, which is a stack
    /// overflow in the one case people will certainly try first.
    /// </summary>
    private void FloodFill(int startX, int startY)
    {
        var target = Editor[startY * 64 + startX];
        if (target == Colour) return;

        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((startX, startY));

        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            if (x < 0 || y < 0 || x >= 64 || y >= 64) continue;

            var at = y * 64 + x;
            if (Editor[at] != target) continue;

            Editor[at] = Colour;

            queue.Enqueue((x + 1, y));
            queue.Enqueue((x - 1, y));
            queue.Enqueue((x, y + 1));
            queue.Enqueue((x, y - 1));
        }
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undo.Count == 0) return;

        _redo.Add((uint[])Editor.Clone());
        Array.Copy(Take(_undo), Editor, Editor.Length);

        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        ShowDrawing();
    }

    [RelayCommand]
    private void Redo()
    {
        if (_redo.Count == 0) return;

        _undo.Add((uint[])Editor.Clone());
        Array.Copy(Take(_redo), Editor, Editor.Length);

        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        ShowDrawing();
    }

    private static uint[] Take(List<uint[]> steps)
    {
        var last = steps[^1];
        steps.RemoveAt(steps.Count - 1);

        return last;
    }

    /// <summary>Starts again from the plain figure the game gives a new account.</summary>
    [RelayCommand]
    private void NewDrawing()
    {
        Array.Clear(Editor);

        // Something to draw on rather than a void.
        //
        // Whole unwraps rather than the front of each part: a body part's six faces are one block
        // of the sheet, and filling only the face of it leaves a figure of flat panels with the
        // sides missing — which is what it looks like, and it looks broken rather than blank.
        Fill(0, 0, 32, 16, Skin);       // head, all six faces
        Fill(8, 0, 8, 8, Hair);         // and hair over the top of it
        Fill(16, 16, 24, 16, Shirt);    // body
        Fill(40, 16, 16, 16, Skin);     // right arm
        Fill(32, 48, 16, 16, Skin);     // left arm
        Fill(0, 16, 16, 16, Trousers);  // right leg
        Fill(16, 48, 16, 16, Trousers); // left leg

        // A face, so the front of the head is the front of the head.
        Fill(9, 12, 2, 1, Eyes);
        Fill(13, 12, 2, 1, Eyes);

        _undo.Clear();
        _redo.Clear();

        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));

        ShowDrawing();
    }

    private const uint Skin = 0xFFC69076u;
    private const uint Hair = 0xFF5A3A26u;
    private const uint Shirt = 0xFF4E7BA8u;
    private const uint Trousers = 0xFF35405Au;
    private const uint Eyes = 0xFF2B2B3Bu;

    private void Fill(int x, int y, int w, int h, uint colour) => Block(x, y, w, h, colour);

    private void Block(int x, int y, int w, int h, uint colour)
    {
        for (var b = 0; b < h; b++)
        for (var a = 0; a < w; a++)
        {
            var px = x + a;
            var py = y + b;

            if (px is >= 0 and < 64 && py is >= 0 and < 64) Editor[py * 64 + px] = colour;
        }
    }

    /// <summary>Called by the view once its file picker has an answer, to draw over a file.</summary>
    public void ImportForEditing(string path)
    {
        Error = null;

        try
        {
            var png = File.ReadAllBytes(path);
            SkinPng.Validate(png);

            ShownModel = GuessModel(png);
            LoadIntoEditor(png);
        }
        catch (Exception e) when (e is SkinException or IOException)
        {
            Error = e.Message;
        }
    }

    /// <summary>Loads whatever the figure is wearing into the editor, to draw over.</summary>
    [RelayCommand]
    private void EditShown()
    {
        if (_shown is not { } png) return;

        LoadIntoEditor(png);
    }

    private void LoadIntoEditor(byte[] png)
    {
        try
        {
            using var skin = SKBitmap.Decode(png);
            if (skin is null) return;

            Array.Clear(Editor);

            for (var y = 0; y < Math.Min(64, skin.Height); y++)
            for (var x = 0; x < Math.Min(64, skin.Width); x++)
            {
                var c = skin.GetPixel(x, y);
                Editor[y * 64 + x] = ((uint)c.Alpha << 24) | ((uint)c.Red << 16) | ((uint)c.Green << 8) | c.Blue;
            }

            // A skin from before 1.8 is only half a sheet: it has no left arm or left leg of its
            // own, and the game draws those by mirroring the right ones. The editor's sheet is
            // always the full size, so without filling them in the two limbs the file never had
            // would simply be missing from the figure — which is exactly what they looked like.
            if (skin.Height < 64)
            {
                MirrorLimb(40, 16, 32, 48);
                MirrorLimb(0, 16, 16, 48);
            }

            _undo.Clear();
            _redo.Clear();

            _beforeDrawing = null;
            Tab = SkinsTab.Draw;
            ShowDrawing();
        }
        catch (ArgumentException e)
        {
            Error = e.Message;
        }
    }

    /// <summary>
    /// Copies a limb to the other side of the body, the way the game mirrors one.
    ///
    /// Mirroring a box is not mirroring its picture: the two side faces trade places and each
    /// face reads the other way along, so it is done face by face rather than by flipping the
    /// block. Every limb is four wide and four deep, which is what the offsets below assume.
    /// </summary>
    private void MirrorLimb(int sx, int sy, int dx, int dy)
    {
        const int w = 4, d = 4, h = 12;

        for (var v = 0; v < h; v++)
        {
            for (var i = 0; i < d; i++)
            {
                Copy(sx + d + w + (d - 1 - i), sy + d + v, dx + i, dy + d + v);
                Copy(sx + (d - 1 - i), sy + d + v, dx + d + w + i, dy + d + v);
            }

            for (var i = 0; i < w; i++)
            {
                Copy(sx + d + (w - 1 - i), sy + d + v, dx + d + i, dy + d + v);
                Copy(sx + d * 2 + w + (w - 1 - i), sy + d + v, dx + d * 2 + w + i, dy + d + v);
            }
        }

        // The top and bottom of the limb, which sit above the four side faces.
        for (var v = 0; v < d; v++)
        for (var i = 0; i < w; i++)
        {
            Copy(sx + d + (w - 1 - i), sy + v, dx + d + i, dy + v);
            Copy(sx + d + w + (w - 1 - i), sy + v, dx + d + w + i, dy + v);
        }

        return;

        void Copy(int fromX, int fromY, int toX, int toY)
        {
            if (fromX is < 0 or >= 64 || fromY is < 0 or >= 64) return;
            if (toX is < 0 or >= 64 || toY is < 0 or >= 64) return;

            Editor[toY * 64 + toX] = Editor[fromY * 64 + fromX];
        }
    }

    /// <summary>The drawing, both as the sheet being drawn on and as the figure wearing it.</summary>
    private void ShowDrawing()
    {
        Sheet = ToBitmap(Bgra(Faded()), 64, 64);

        // The pick map survives a change of colour: the shape did not move, only its paint. It is
        // only thrown away when the figure turns, which is what Redraw does.
        var keep = _pick;
        Show(EditorPng(), ShownModel);
        _pick = keep;
    }

    /// <summary>
    /// The sheet with the layer that is not being drawn on turned down. Both stay visible —
    /// drawing a hood over a head you cannot see is guesswork — but only one of them looks live.
    /// </summary>
    private uint[] Faded()
    {
        var shown = new uint[Editor.Length];
        var live = DrawingOuter ? OuterParts : BodyParts;

        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var at = y * 64 + x;
            var colour = Editor[at];

            shown[at] = In(live, x, y)
                ? colour
                : (colour & 0x00FFFFFFu) | (uint)((byte)(colour >> 24) / 4 << 24);
        }

        return shown;
    }

    private byte[] EditorPng()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        var bytes = Bgra(Editor);

        Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        return data.ToArray();
    }

    private static byte[] Bgra(uint[] argb)
    {
        var bytes = new byte[argb.Length * 4];

        for (var i = 0; i < argb.Length; i++)
        {
            bytes[i * 4] = (byte)argb[i];
            bytes[i * 4 + 1] = (byte)(argb[i] >> 8);
            bytes[i * 4 + 2] = (byte)(argb[i] >> 16);
            bytes[i * 4 + 3] = (byte)(argb[i] >> 24);
        }

        return bytes;
    }

    private static WriteableBitmap ToBitmap(byte[] bgra, int width, int height)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);

        using (var locked = bitmap.Lock()) Marshal.Copy(bgra, 0, locked.Address, bgra.Length);

        return bitmap;
    }

    /// <summary>Hands the drawing to the library, which is also how it leaves this tab.</summary>
    [RelayCommand]
    private void SaveDrawing()
    {
        Error = null;

        try
        {
            var card = Card(_library.Save(EditorPng(), "My drawing", ShownModel));

            Mine.Insert(0, card);
            OnPropertyChanged(nameof(HasNoSkins));

            Status = "Saved to My Skins.";
            _ = DrawThumbnailsAsync();
        }
        catch (Exception e) when (e is SkinException or IOException)
        {
            Error = e.Message;
        }
    }

    /// <summary>Called by the view once its save dialog has an answer.</summary>
    public void ExportTo(string path)
    {
        try
        {
            File.WriteAllBytes(path, _shown ?? EditorPng());
            Status = $"Written to {Path.GetFileName(path)}.";
        }
        catch (IOException e)
        {
            Error = e.Message;
        }
    }

    public void OnAccountChanged()
    {
        OnPropertyChanged(nameof(CanWear));
        OnPropertyChanged(nameof(WearNote));

        _ = ShowCurrentSkinAsync();
    }
}
