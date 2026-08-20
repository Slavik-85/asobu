using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Asobu.App.Controls;
using Asobu.Core;
using Asobu.Core.Accounts;
using Asobu.Core.Online;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Asobu.App.ViewModels;

/// <summary>One person in the friends page, whichever section they sit in.</summary>
public partial class FriendRow(Friend friend) : ViewModelBase
{
    public string Uuid => friend.Uuid;
    public string Name => friend.Name;
    public bool Online => friend.Online;

    public string PresenceLabel => friend.Online ? "Online" : Ago(friend.LastSeen);

    [ObservableProperty] public partial Bitmap? Face { get; set; }
    public bool HasFace => Face is not null;
    partial void OnFaceChanged(Bitmap? value) => OnPropertyChanged(nameof(HasFace));

    private static string Ago(DateTimeOffset seen)
    {
        var gone = DateTimeOffset.UtcNow - seen;
        if (gone < TimeSpan.FromMinutes(2)) return "Just now";
        if (gone < TimeSpan.FromHours(1)) return $"Seen {(int)gone.TotalMinutes} min ago";
        if (gone < TimeSpan.FromHours(48)) return $"Seen {(int)gone.TotalHours} h ago";
        if (gone < TimeSpan.FromDays(365)) return $"Seen {(int)gone.TotalDays} d ago";
        return "Long gone";
    }
}

/// <summary>
/// The friends page. Signing in to the network is automatic: the first visit with a Microsoft
/// account proves who you are through Mojang and that's that — there is no second password and
/// no separate Asobu account to make.
/// </summary>
public partial class FriendsViewModel : ViewModelBase
{
    private readonly AsobuLauncher _launcher;
    private readonly AccountsViewModel _accounts;
    private readonly Action _goAccounts;

    /// <summary>
    /// Keeps presence honest while the launcher sits open on some other page: every tick tells
    /// the server we're alive and hears who else is. Started after the first successful
    /// connection and never stopped — a tick while signed out just does nothing.
    /// </summary>
    private readonly DispatcherTimer _heartbeat;

    /// <summary>What the list currently shows, so an unchanged answer doesn't rebuild it.</summary>
    private string _shown = "";

    public FriendsViewModel(AsobuLauncher launcher, AccountsViewModel accounts, Action goAccounts)
    {
        _launcher = launcher;
        _accounts = accounts;
        _goAccounts = goAccounts;

        _heartbeat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _heartbeat.Tick += (_, _) => _ = RefreshAsync(quiet: true);
    }

    public ObservableCollection<FriendRow> Friends { get; } = [];
    public ObservableCollection<FriendRow> Incoming { get; } = [];
    public ObservableCollection<FriendRow> Outgoing { get; } = [];

    [ObservableProperty] public partial bool IsConnected { get; set; }
    [ObservableProperty] public partial bool IsConnecting { get; set; }
    [ObservableProperty] public partial string? ConnectionError { get; set; }

    [ObservableProperty] public partial string NewFriendName { get; set; } = "";
    [ObservableProperty] public partial string? Notice { get; set; }
    [ObservableProperty] public partial bool NoticeIsGood { get; set; }

    /// <summary>The network runs on proven Minecraft identities, which offline accounts don't have.</summary>
    public bool NeedsMicrosoft => _accounts.Active is not { Kind: AccountKind.Microsoft };

    public bool IsEmpty => IsConnected && Friends.Count == 0 && Incoming.Count == 0 && Outgoing.Count == 0;
    public bool HasFriends => Friends.Count > 0;
    public bool HasIncoming => Incoming.Count > 0;
    public bool HasOutgoing => Outgoing.Count > 0;
    public string FriendsHeading => $"Friends · {Friends.Count}";

    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    /// <summary>Every visit to the page lands here: connect if needed, then bring the list up to date.</summary>
    public void Opened()
    {
        Notice = null;
        _ = EnsureConnectedAsync(thenRefresh: true);
    }

    /// <summary>
    /// Called at startup with only the stored session — one request when it exists, nothing at
    /// all when it doesn't. No Microsoft refresh and no Mojang round-trip, so it cannot slow
    /// the window down; it just means returning users show as online without opening this page.
    /// </summary>
    public async Task TryResumeAsync()
    {
        if (_accounts.Active is not { Kind: AccountKind.Microsoft } account) return;

        if (!await _launcher.Friends.TryResumeAsync(account).ConfigureAwait(false)) return;

        // This is called from the startup warm-up's worker thread, and everything from here on
        // is UI state — the timer, the flags, the rows.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsConnected = true;
            _heartbeat.Start();
            return RefreshAsync(quiet: true);
        });
    }

    /// <summary>The active account changed under us; whatever was shown belongs to someone else.</summary>
    public void OnAccountChanged()
    {
        IsConnected = false;
        ConnectionError = null;
        Notice = null;
        _shown = "";
        Friends.Clear();
        Incoming.Clear();
        Outgoing.Clear();
        RaiseCounts();
        OnPropertyChanged(nameof(NeedsMicrosoft));

        _ = TryResumeAsync();
    }

    [RelayCommand]
    private void GoAccounts() => _goAccounts();

    [RelayCommand]
    private Task Retry() => EnsureConnectedAsync(thenRefresh: true);

    private async Task EnsureConnectedAsync(bool thenRefresh)
    {
        OnPropertyChanged(nameof(NeedsMicrosoft));
        if (NeedsMicrosoft || IsConnecting) return;

        if (!IsConnected)
        {
            IsConnecting = true;
            ConnectionError = null;
            try
            {
                var account = _accounts.Active!;
                if (!await _launcher.Friends.TryResumeAsync(account))
                {
                    // The full handshake. The Minecraft token this refreshes goes to Mojang
                    // and nowhere else.
                    var session = await _launcher.ResolveSessionAsync(account);
                    await _launcher.Friends.ConnectAsync(session);
                }
                IsConnected = true;
                _heartbeat.Start();
            }
            catch (FriendsException e)
            {
                ConnectionError = e.Message;
                return;
            }
            catch (Exception)
            {
                ConnectionError = "Couldn't join the network. Try again in a moment.";
                return;
            }
            finally
            {
                IsConnecting = false;
            }
        }

        if (thenRefresh) await RefreshAsync(quiet: false);
    }

    private async Task RefreshAsync(bool quiet)
    {
        if (!IsConnected) return;

        try
        {
            Show(await _launcher.Friends.GetFriendsAsync());
        }
        catch (FriendsAuthException)
        {
            // The stored session aged out. Reconnect once, silently; the heartbeat or the next
            // visit picks it up from there.
            IsConnected = false;
            if (!quiet) await EnsureConnectedAsync(thenRefresh: true);
        }
        catch (FriendsException e)
        {
            if (!quiet) ConnectionError = e.Message;
        }
    }

    private void Show(FriendsSnapshot snapshot)
    {
        // Online friends first, then alphabetical — the people you can actually play with now.
        var friends = snapshot.Friends
            .OrderByDescending(f => f.Online)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // An unchanged answer must not rebuild the rows: the heartbeat refreshes every minute,
        // and yanking rows out from under a hovered button is how misclicks are made.
        var signature = string.Join(",",
            friends.Select(f => f.Uuid + f.Online)
                .Concat(snapshot.Incoming.Select(f => "i" + f.Uuid))
                .Concat(snapshot.Outgoing.Select(f => "o" + f.Uuid)));
        if (signature == _shown) return;
        _shown = signature;

        Rebuild(Friends, friends);
        Rebuild(Incoming, snapshot.Incoming);
        Rebuild(Outgoing, snapshot.Outgoing);
        RaiseCounts();
    }

    private void Rebuild(ObservableCollection<FriendRow> rows, IEnumerable<Friend> people)
    {
        rows.Clear();
        foreach (var person in people)
        {
            var row = new FriendRow(person);
            rows.Add(row);
            _ = LoadFaceAsync(row);
        }
    }

    private async Task LoadFaceAsync(FriendRow row)
    {
        var face = await SkinFaces.ForAsync(_launcher.Http, row.Uuid);
        if (face is not null) Dispatcher.UIThread.Post(() => row.Face = face);
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasFriends));
        OnPropertyChanged(nameof(HasIncoming));
        OnPropertyChanged(nameof(HasOutgoing));
        OnPropertyChanged(nameof(FriendsHeading));
    }

    [RelayCommand]
    private async Task AddFriendAsync()
    {
        var name = NewFriendName.Trim();
        if (name.Length == 0) return;

        try
        {
            await _launcher.Friends.AddAsync(name);
            NewFriendName = "";
            NoticeIsGood = true;
            Notice = $"Request sent to {name}.";
            await RefreshAsync(quiet: true);
        }
        catch (FriendsAuthException)
        {
            IsConnected = false;
            await EnsureConnectedAsync(thenRefresh: false);
            if (IsConnected) await AddFriendAsync();
        }
        catch (FriendsException e)
        {
            NoticeIsGood = false;
            Notice = e.Message;
        }
    }

    [RelayCommand]
    private async Task AcceptAsync(FriendRow row)
    {
        try
        {
            await _launcher.Friends.AcceptAsync(row.Uuid);
            await RefreshAsync(quiet: true);
        }
        catch (FriendsException e)
        {
            NoticeIsGood = false;
            Notice = e.Message;
        }
    }

    /// <summary>Unfriend, cancel, or decline — one action, because it's one wish: not this person.</summary>
    [RelayCommand]
    private async Task RemoveAsync(FriendRow row)
    {
        try
        {
            await _launcher.Friends.RemoveAsync(row.Uuid);
            await RefreshAsync(quiet: true);
        }
        catch (FriendsException e)
        {
            NoticeIsGood = false;
            Notice = e.Message;
        }
    }
}
