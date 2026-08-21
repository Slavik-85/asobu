using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
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

    /// <summary>Things said while their conversation was not the one on screen.</summary>
    [ObservableProperty] public partial int Unread { get; set; }

    public bool HasUnread => Unread > 0;
    public string UnreadLabel => Unread > 9 ? "9+" : Unread.ToString();

    partial void OnUnreadChanged(int value)
    {
        OnPropertyChanged(nameof(HasUnread));
        OnPropertyChanged(nameof(UnreadLabel));
    }

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
    /// Stops the watch when there is nothing left to watch for: the account changed, or the
    /// launcher is closing.
    /// </summary>
    private CancellationTokenSource? _watching;

    /// <summary>The revision the list on screen was true at. The watch waits for anything newer.</summary>
    private long _revision;

    /// <summary>What the list currently shows, so an unchanged answer doesn't rebuild it.</summary>
    private string _shown = "";

    public FriendsViewModel(AsobuLauncher launcher, AccountsViewModel accounts, Action goAccounts)
    {
        _launcher = launcher;
        _accounts = accounts;
        _goAccounts = goAccounts;
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

    /// <summary>
    /// Watches for changes until told to stop.
    ///
    /// One request at a time, held open by the server until something happens or about twenty
    /// seconds pass. A friend request therefore appears on the other screen in the time it takes
    /// to travel there, rather than whenever a timer next happened to fire.
    /// </summary>
    private void StartWatching()
    {
        if (_watching is { IsCancellationRequested: false }) return;

        var stopping = new CancellationTokenSource();
        _watching = stopping;

        _ = Task.Run(async () =>
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    var snapshot = await _launcher.Friends
                        .WatchAsync(_revision, stopping.Token)
                        .ConfigureAwait(false);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        _revision = snapshot.Revision;
                        Show(snapshot);
                    });
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (FriendsAuthException)
                {
                    // The session aged out. Stop rather than spin; opening the page reconnects.
                    await Dispatcher.UIThread.InvokeAsync(() => IsConnected = false);
                    return;
                }
                catch (Exception)
                {
                    // Offline, or the server is restarting. Wait before asking again so a
                    // network that is down is not hammered by a launcher left open.
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15), stopping.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        });
    }

    private void StopWatching()
    {
        _watching?.Cancel();
        _watching = null;
    }

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
        // is UI state: the watch, the flags, the rows.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsConnected = true;
            StartWatching();
            return RefreshAsync(quiet: true);
        });
    }

    /// <summary>The active account changed under us; whatever was shown belongs to someone else.</summary>
    public void OnAccountChanged()
    {
        // Whatever the old account was watching for is no longer anybody's business here.
        StopWatching();
        _revision = 0;

        IsConnected = false;
        ConnectionError = null;
        Notice = null;
        _shown = "";

        // Somebody else's conversations are none of the new account's business, and they only
        // ever existed in memory anyway.
        _conversations.Clear();
        CloseChat();

        Friends.Clear();
        Incoming.Clear();
        Outgoing.Clear();
        RaiseCounts();
        OnPropertyChanged(nameof(NeedsMicrosoft));

        _ = TryResumeAsync();
    }

    // ---- Chat ----

    /// <summary>One thing said, on one side or the other.</summary>
    public sealed class ChatLine(string text, bool mine, DateTimeOffset at)
    {
        public string Text { get; } = text;

        /// <summary>Ours, so it sits on the right in the accent colour. Theirs sits on the left.</summary>
        public bool Mine { get; } = mine;

        public bool Theirs => !Mine;
        public string TimeLabel { get; } = at.ToLocalTime().ToString("HH:mm");
    }

    /// <summary>
    /// What has been said, by friend, for as long as the launcher is open.
    ///
    /// Only here. The server relays chat and stores none of it, so there is nothing to catch up
    /// on when Asobu next starts — closing it is the end of the conversation, and this is
    /// deliberately not written to disk either. Somebody who wants a record of what was said has
    /// a hundred better apps for it; this is for "launching in five".
    /// </summary>
    private readonly Dictionary<string, ObservableCollection<ChatLine>> _conversations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The friend whose conversation is on screen, or null when the list is.</summary>
    [ObservableProperty] public partial FriendRow? ChatWith { get; set; }

    [ObservableProperty] public partial ObservableCollection<ChatLine>? Conversation { get; set; }
    [ObservableProperty] public partial string Draft { get; set; } = "";
    [ObservableProperty] public partial string? ChatError { get; set; }

    public bool IsChatting => ChatWith is not null;
    public bool CanSend => IsChatting && Draft.Trim().Length > 0;
    public bool HasChatError => ChatError is { Length: > 0 };

    /// <summary>Said once, in the empty conversation, so the terms are clear before anyone types.</summary>
    public bool ConversationIsEmpty => Conversation is { Count: 0 };

    partial void OnChatWithChanged(FriendRow? value)
    {
        OnPropertyChanged(nameof(IsChatting));
        OnPropertyChanged(nameof(CanSend));
    }

    partial void OnDraftChanged(string value) => OnPropertyChanged(nameof(CanSend));
    partial void OnChatErrorChanged(string? value) => OnPropertyChanged(nameof(HasChatError));
    partial void OnConversationChanged(ObservableCollection<ChatLine>? value) =>
        OnPropertyChanged(nameof(ConversationIsEmpty));

    private ObservableCollection<ChatLine> ConversationFor(string uuid) =>
        _conversations.TryGetValue(uuid, out var existing)
            ? existing
            : _conversations[uuid] = [];

    [RelayCommand]
    private void OpenChat(FriendRow? row)
    {
        if (row is null) return;

        ChatError = null;
        Draft = "";
        ChatWith = row;
        Conversation = ConversationFor(row.Uuid);

        // Reading them is what marks them read.
        row.Unread = 0;

        OnPropertyChanged(nameof(ConversationIsEmpty));
    }

    [RelayCommand]
    private void CloseChat()
    {
        ChatWith = null;
        Conversation = null;
        ChatError = null;
    }

    /// <summary>
    /// Files what just arrived, and counts it against whoever said it unless their conversation
    /// is the one being looked at.
    /// </summary>
    private void Deliver(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0) return;

        foreach (var message in messages)
        {
            ConversationFor(message.From).Add(new ChatLine(message.Text, mine: false, message.At));

            if (string.Equals(ChatWith?.Uuid, message.From, StringComparison.OrdinalIgnoreCase))
            {
                OnPropertyChanged(nameof(ConversationIsEmpty));
                continue;
            }

            // Not on screen, so it is news. The row may not exist yet — a message from somebody
            // whose acceptance is in the same snapshot — and the badge lands when it does.
            if (Friends.FirstOrDefault(f =>
                    string.Equals(f.Uuid, message.From, StringComparison.OrdinalIgnoreCase)) is { } row)
                row.Unread++;
        }
    }

    [RelayCommand]
    private async Task SendChatAsync()
    {
        if (ChatWith is not { } friend || Draft.Trim() is not { Length: > 0 } text) return;

        // On screen before it is on the wire. Chat that waits for a round trip before showing
        // what you typed feels broken on a slow connection, and this one cannot be un-said
        // anyway — the server has no record to correct.
        ConversationFor(friend.Uuid).Add(new ChatLine(text, mine: true, DateTimeOffset.UtcNow));
        Draft = "";
        ChatError = null;
        OnPropertyChanged(nameof(ConversationIsEmpty));

        try
        {
            await _launcher.Friends.SayAsync(friend.Uuid, text);
        }
        catch (FriendsException e)
        {
            ChatError = e.Message;
        }
        catch (Exception e)
        {
            ChatError = e.Message;
        }
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
                StartWatching();
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
            var snapshot = await _launcher.Friends.GetFriendsAsync();
            _revision = snapshot.Revision;
            Show(snapshot);
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
        // Before anything else, and before the early return below. A message arriving on its own
        // leaves the friends list identical, so the signature check would drop it — and the
        // server has already forgotten it by then, which makes dropped mean gone.
        Deliver(snapshot.Messages);

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
        // What each row was carrying before it was thrown away. A row is rebuilt whenever anyone
        // comes online, and without this every unread badge would vanish the moment a friend's
        // presence changed — including the badge belonging to the message that caused it.
        var unread = rows.ToDictionary(row => row.Uuid, row => row.Unread, StringComparer.OrdinalIgnoreCase);

        rows.Clear();
        foreach (var person in people)
        {
            var row = new FriendRow(person) { Unread = unread.GetValueOrDefault(person.Uuid) };
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
