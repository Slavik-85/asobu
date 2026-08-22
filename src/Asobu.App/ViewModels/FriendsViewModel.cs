using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Asobu.App.Controls;
using Asobu.Core;
using Asobu.Core.Accounts;
using Asobu.Core.Hosting;
using Asobu.Core.Instances;
using Asobu.Core.Mods;
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

    /// <summary>
    /// The handle rather than the bare name: an offline account is shown as name#tag everywhere,
    /// so one carrying no proof of who it is never sits in the list looking exactly like one that
    /// does. It is also what a person would type to add them.
    /// </summary>
    public string Name => friend.Handle;

    public bool Online => friend.Online;

    /// <summary>
    /// The bare Minecraft name, without the tag. This is what they will arrive at a world under,
    /// so it is what a pass has to be signed for — the handle would never match.
    /// </summary>
    public string PlayerName => friend.Name;

    /// <summary>The world they have open, kept current in place so a count ticking over doesn't rebuild the row.</summary>
    [ObservableProperty] public partial FriendWorld? World { get; set; }

    /// <summary>They have let me in. Set only for a world of theirs, never for mine.</summary>
    public bool CanJoin => World is { AmInvited: true };

    public bool IsHosting => World is not null;

    /// <summary>"Skyblock · 2/8", the whole of what a friend's world looks like in a list.</summary>
    public string WorldLabel => World is null ? "" : $"{World.Name} · {World.Busy}";

    /// <summary>Whether I have invited them to mine. Mine to set, so it is not read from them.</summary>
    [ObservableProperty] public partial bool IsInvited { get; set; }

    public string InviteLabel => IsInvited ? "Invited" : "Invite";

    partial void OnIsInvitedChanged(bool value) => OnPropertyChanged(nameof(InviteLabel));

    partial void OnWorldChanged(FriendWorld? value)
    {
        OnPropertyChanged(nameof(CanJoin));
        OnPropertyChanged(nameof(IsHosting));
        OnPropertyChanged(nameof(WorldLabel));
    }

    /// <summary>Their chat key, or null when they have not published one.</summary>
    public string? PublicKey => friend.PublicKey;

    /// <summary>
    /// Whether anything can be sent to them. False for a friend on a launcher old enough not to
    /// have a key: there is no way to write to them that only they could read, and sending
    /// something readable instead would quietly break the promise the feature makes.
    /// </summary>
    public bool CanReceive => friend.PublicKey is { Length: > 0 };

    public string PresenceLabel => friend.Online ? "Online" : Ago(friend.LastSeen);

    [ObservableProperty] public partial Bitmap? Face { get; set; }
    public bool HasFace => Face is not null;
    partial void OnFaceChanged(Bitmap? value) => OnPropertyChanged(nameof(HasFace));

    /// <summary>
    /// The X has been pressed once and is waiting to be meant. Only friends arm like this —
    /// declining a request or cancelling one costs nothing to get wrong, where unfriending
    /// somebody takes both of you to undo.
    /// </summary>
    [ObservableProperty] public partial bool IsConfirmingRemove { get; set; }

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
    /// The same sheet the Servers tab uses, and joining goes the same way — through the instances
    /// page, because the progress, the log and the running state all live over there.
    /// </summary>
    private readonly AskInstall _askJoin;
    private readonly Func<Instance, string, Task<string?>> _join;

    /// <summary>Chat history on this computer. Never sent anywhere; see ChatArchive.</summary>
    private readonly ChatArchive _archive;

    /// <summary>
    /// Stops the watch when there is nothing left to watch for: the account changed, or the
    /// launcher is closing.
    /// </summary>
    private CancellationTokenSource? _watching;

    /// <summary>The revision the list on screen was true at. The watch waits for anything newer.</summary>
    private long _revision;

    /// <summary>What the list currently shows, so an unchanged answer doesn't rebuild it.</summary>
    private string _shown = "";

    public FriendsViewModel(
        AsobuLauncher launcher,
        AccountsViewModel accounts,
        Action goAccounts,
        AskInstall askJoin,
        Func<Instance, string, Task<string?>> join)
    {
        _launcher = launcher;
        _accounts = accounts;
        _goAccounts = goAccounts;
        _askJoin = askJoin;
        _join = join;
        _archive = new ChatArchive(launcher.Paths);
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

    /// <summary>Nothing signed in at all, so there is nobody to be on the network as.</summary>
    public bool NeedsAccount => _accounts.Active is null;

    /// <summary>
    /// An offline account that has not asked to be on the network yet.
    ///
    /// Asking is the whole of it, and it is asked once. An offline account is a local thing —
    /// a name typed into a box — and a launcher that announced every name anyone had ever played
    /// under would be publishing a list nobody chose to publish.
    /// </summary>
    public bool NeedsOfflineJoin =>
        _accounts.Active is { Kind: AccountKind.Offline, NetworkUuid: null or "" };

    /// <summary>What this account is called on the network — name#tag when it is an offline one.</summary>
    public string MyHandle => _accounts.Active?.NetworkHandle ?? "";

    /// <summary>True once an offline account is on the network, which is when its tag is worth showing.</summary>
    public bool ShowsHandle => _accounts.Active is { Kind: AccountKind.Offline, NetworkTag: { Length: > 0 } };

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

    /// <summary>
    /// Makes sure this launcher has a chat key and that the server has its public half.
    ///
    /// Run on every connect. The key itself is made once and kept; publishing it again costs one
    /// request the server discards when it is unchanged, which is a great deal cheaper than a
    /// friend being unable to write to somebody because a key never arrived.
    /// </summary>
    private async Task EnsureChatKeyAsync()
    {
        if (_accounts.Active is not { } account) return;

        try
        {
            _chatKey ??= new MessageCrypto(new TokenVault(_launcher.Paths)).MineFor(account.Uuid);
            _myPublicKey = MessageCrypto.PublicKeyOf(_chatKey);

            await _launcher.Friends.PublishKeyAsync(_myPublicKey);
        }
        catch (Exception)
        {
            // Offline, or an older server without the endpoint. Chat will say it cannot send
            // rather than sending something readable, which is the right way to fail.
        }
    }

    private void StopWatching()
    {
        _watching?.Cancel();
        _watching = null;
    }

    /// <summary>
    /// Whether the drawer is actually up.
    ///
    /// Kept because a conversation can be left open behind a closed drawer, and "the message is
    /// already on screen" has to mean on screen rather than merely selected.
    /// </summary>
    private bool IsOnScreen { get; set; }

    /// <summary>Every visit to the page lands here: connect if needed, then bring the list up to date.</summary>
    public void Opened()
    {
        Notice = null;
        IsOnScreen = true;

        // Whatever arrived for the conversation still open behind the drawer is now being looked
        // at, so it stops counting.
        if (ChatWith is { } open)
        {
            open.Unread = 0;
            RaiseUnread();
        }

        _ = EnsureConnectedAsync(thenRefresh: true);
    }

    /// <summary>The drawer has gone. The watch keeps running; nothing here is on screen any more.</summary>
    public void Closed() => IsOnScreen = false;

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
            StartHosting();
            _ = EnsureChatKeyAsync();
            return RefreshAsync(quiet: true);
        });
    }

    /// <summary>The active account changed under us; whatever was shown belongs to someone else.</summary>
    public void OnAccountChanged()
    {
        // Whatever the old account was watching for is no longer anybody's business here.
        StopWatching();
        StopHosting();
        _revision = 0;

        IsConnected = false;
        ConnectionError = null;
        Notice = null;
        _shown = "";

        // Somebody else's conversations are none of the new account's business, and they only
        // ever existed in memory anyway.
        _conversations.Clear();
        CloseChat();
        Disarm();

        // Somebody else's key is no use to this account, and holding it would have messages
        // sealed to the wrong person.
        _chatKey?.Dispose();
        _chatKey = null;
        _myPublicKey = null;

        Friends.Clear();
        Incoming.Clear();
        Outgoing.Clear();
        RaiseCounts();
        RaiseGates();

        _ = TryResumeAsync();
    }

    // ---- Hosting a world ----

    /// <summary>
    /// How long a pass is good for. Long enough that nobody is thrown out mid-session, short
    /// enough that a copy left lying around stops working the same day.
    ///
    /// ponytail: un-inviting takes the world off their screen at once, but a pass they already
    /// held keeps opening the door until this runs out. Add a revocation set on the doorman if
    /// that gap ever matters.
    /// </summary>
    private static readonly TimeSpan PassLife = TimeSpan.FromHours(12);

    private WorldHost? _worldHost;
    private CancellationTokenSource? _hostLoop;
    private byte[]? _hostSecret;

    /// <summary>The tunnel into a friend's world, held open for as long as the game is in it.</summary>
    private WorldJoin? _joined;

    /// <summary>The world this launcher has open, or null. Nobody presses anything to set it.</summary>
    [ObservableProperty] public partial HostedWorld? MyWorld { get; set; }

    public bool IsHostingWorld => MyWorld is not null;

    public string MyWorldLabel => MyWorld is null
        ? ""
        : $"{MyWorld.Name} · {(MyWorld.MaxPlayers > 0 ? $"{MyWorld.Players}/{MyWorld.MaxPlayers}" : MyWorld.Players.ToString())}";

    partial void OnMyWorldChanged(HostedWorld? value)
    {
        OnPropertyChanged(nameof(IsHostingWorld));
        OnPropertyChanged(nameof(MyWorldLabel));
    }

    /// <summary>
    /// Starts listening for a world being opened to LAN. Runs for as long as the launcher is
    /// signed in rather than being switched on: the whole promise is that opening a world is the
    /// only step, so there is nothing here for anybody to remember to press first.
    /// </summary>
    private void StartHosting()
    {
        if (_worldHost is not null || _accounts.Active is not { } account) return;

        _hostSecret = HostSecret.For(new TokenVault(_launcher.Paths), account.Uuid);

        var stopping = new CancellationTokenSource();
        _hostLoop = stopping;

        var host = new WorldHost(_hostSecret, account.Username, ports: _launcher.LanPorts);
        host.Changed += world => Dispatcher.UIThread.Post(() => _ = PublishWorldAsync(world));
        _worldHost = host;

        _ = host.RunAsync(stopping.Token);
    }

    private void StopHosting()
    {
        _hostLoop?.Cancel();
        _hostLoop = null;

        _worldHost?.Dispose();
        _worldHost = null;

        _joined?.Dispose();
        _joined = null;

        MyWorld = null;
        foreach (var row in Friends) row.IsInvited = false;
    }

    /// <summary>
    /// Tells the network what is open, or that nothing is. Failing here is not worth showing: the
    /// next heartbeat says the same thing a few seconds later, and a world that stops being
    /// announced drops off everyone's list on its own.
    /// </summary>
    private async Task PublishWorldAsync(HostedWorld? world)
    {
        MyWorld = world;
        if (world is null)
        {
            foreach (var row in Friends) row.IsInvited = false;
        }

        if (!IsConnected) return;

        try
        {
            if (world is null)
                await _launcher.Friends.CloseWorldAsync();
            else
                await _launcher.Friends.OpenWorldAsync(
                    world.Name, world.Players, world.MaxPlayers, world.DoormanPort, world.Version);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Lets somebody in, or shuts the door on them again.</summary>
    [RelayCommand]
    private async Task InviteAsync(FriendRow? row)
    {
        if (row is null || _hostSecret is null || MyWorld is null) return;

        try
        {
            if (row.IsInvited)
            {
                await _launcher.Friends.UninviteAsync(row.Uuid);
                row.IsInvited = false;
                return;
            }

            // Signed for their name as well as their id, because the doorman checks the name the
            // player actually arrives under — a pass naming nobody would open for anybody holding it.
            var pass = InviteToken.Mint(
                _hostSecret, row.Uuid, row.PlayerName, DateTimeOffset.UtcNow.Add(PassLife));

            await _launcher.Friends.InviteAsync(row.Uuid, pass);
            row.IsInvited = true;
        }
        catch (FriendsException e)
        {
            Notice = e.Message;
            NoticeIsGood = false;
        }
    }

    /// <summary>
    /// Joins a friend's world, through the same sheet the Servers tab uses. Finding a way through
    /// happens after an instance is chosen rather than before: it takes a moment, and a moment
    /// spent before the question is a moment spent on a join that might be cancelled.
    /// </summary>
    [RelayCommand]
    private void Join(FriendRow? row)
    {
        if (row?.World is not { AmInvited: true } world) return;

        _askJoin(
            $"Join {row.Name}",
            async instance =>
            {
                WorldJoin reached;
                try
                {
                    reached = await WorldJoin.ReachAsync(world.Addresses, world.Pass!);
                }
                catch (WorldJoinException e)
                {
                    return e.Message;
                }

                // Held on the page rather than in the sheet: the tunnel has to outlive the click
                // that made it, for as long as the player is in the world.
                _joined?.Dispose();
                _joined = reached;

                return await _join(instance, reached.Address);
            },
            _ => Task.FromResult(SupportFor(world)));
    }

    /// <summary>
    /// Which instances could join, in the shape the sheet already understands.
    ///
    /// A world that could not be asked its version, or that reports itself as something no
    /// instance is named after — a modded server often does — greys nothing rather than
    /// everything. Refusing every instance over a name we failed to recognise would block a join
    /// that was going to work.
    /// </summary>
    private ModSupport SupportFor(FriendWorld world)
    {
        var mine = _launcher.Instances.LoadAll()
            .Select(instance => instance.MinecraftVersion)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var matching = mine
            .Where(version => version.Equals(world.Version, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var accepted = (matching.Count > 0 ? matching : mine)
            .Select(version => new ModVersion(
                ModProvider.Modrinth, world.Name, version, [version],

                // No loader named, which the sheet reads as "runs on any".
                [], null, 0, null, "", null, 0, ModChannel.Release))
            .ToList();

        return ModSupport.From(accepted);
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

        /// <summary>The picture, for a line that is one. Decoded once, when it arrives.</summary>
        public Bitmap? Picture { get; init; }

        public bool IsPicture => Picture is not null;
        public bool IsText => Picture is null;

        /// <summary>
        /// Built here rather than in the view so a picture that will not decode still shows as
        /// something. A message that arrived and rendered as nothing at all would read as one
        /// that never came.
        /// </summary>
        public static ChatLine ForImage(byte[] jpeg, bool mine, DateTimeOffset at)
        {
            try
            {
                using var stream = new System.IO.MemoryStream(jpeg);

                return new ChatLine("", mine, at) { Picture = new Bitmap(stream) };
            }
            catch (Exception)
            {
                return new ChatLine("[a picture that couldn't be opened]", mine, at);
            }
        }

        /// <summary>The same, for a picture coming back off the disk rather than off the wire.</summary>
        public static ChatLine ForImageFile(string path, bool mine, DateTimeOffset at)
        {
            try
            {
                return new ChatLine("", mine, at) { Picture = new Bitmap(path) };
            }
            catch (Exception)
            {
                return new ChatLine("[a picture that couldn't be opened]", mine, at);
            }
        }
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

    /// <summary>
    /// This account's chat key. The private half stays here and in the vault; the public half is
    /// published so friends can write to it.
    /// </summary>
    private ECDiffieHellman? _chatKey;

    /// <summary>What this launcher published, for working out the fingerprint of a conversation.</summary>
    private string? _myPublicKey;

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
        OnPropertyChanged(nameof(Fingerprint));
        OnPropertyChanged(nameof(HasFingerprint));
        OnPropertyChanged(nameof(CanReachThem));
    }

    /// <summary>Whether the person on screen can be written to at all.</summary>
    public bool CanReachThem => ChatWith is { CanReceive: true };

    partial void OnDraftChanged(string value) => OnPropertyChanged(nameof(CanSend));
    partial void OnChatErrorChanged(string? value) => OnPropertyChanged(nameof(HasChatError));
    partial void OnConversationChanged(ObservableCollection<ChatLine>? value) =>
        OnPropertyChanged(nameof(ConversationIsEmpty));

    /// <summary>What one message turns out to be, or a note in its place when it will not open.</summary>
    private ChatLine Unseal(ChatMessage message)
    {
        ChatLine Note(string what) => new(what, mine: false, message.At);

        if (_chatKey is null) return Note("[can't read this — no key on this launcher]");

        var sender = Friends.FirstOrDefault(f =>
            string.Equals(f.Uuid, message.From, StringComparison.OrdinalIgnoreCase));

        if (sender?.PublicKey is not { Length: > 0 } theirs)
            return Note("[can't read this — no key published for " + message.Name + "]");

        if (MessageCrypto.Unseal(_chatKey, theirs, message.Box) is not { } payload)
            return Note("[couldn't be decrypted — they may have changed keys]");

        // Kept here rather than at the caller: this is the one place that knows whether what
        // arrived was words or a picture, and the notes above about keys are not messages —
        // they are this launcher talking to itself, and archiving them would be odd.
        if (payload.Kind == ChatKind.Image)
        {
            _archive.AppendPicture(ArchiveKey, message.From, message.At, mine: false, payload.Content);
            return ChatLine.ForImage(payload.Content, mine: false, message.At);
        }

        var said = payload.AsText();
        _archive.Append(ArchiveKey, message.From, message.At, mine: false, said);
        return new ChatLine(said, mine: false, message.At);
    }

    /// <summary>The longest a message may be, matched to what the server will carry once sealed.</summary>
    private const int MaxMessageLength = 2000;

    /// <summary>
    /// Trims and takes out the characters that are not text.
    ///
    /// Done here because here is the only place it can be. The server used to do it and now sees
    /// nothing but ciphertext, so a control character reaching a text renderer is this method's
    /// to prevent or nobody's.
    /// </summary>
    private static string Clean(string text)
    {
        var kept = new System.Text.StringBuilder(text.Length);

        foreach (var c in text)
            if (c is '\n' or '\t' || !char.IsControl(c))
                kept.Append(c);

        return kept.ToString().Trim();
    }

    /// <summary>
    /// The code both ends of this conversation can read out to each other.
    ///
    /// The one check that catches a server handing out the wrong key, which is the hole nothing
    /// else here can close. Empty until both keys are known.
    /// </summary>
    public string? Fingerprint =>
        _myPublicKey is { Length: > 0 } mine && ChatWith?.PublicKey is { Length: > 0 } theirs
            ? MessageCrypto.Fingerprint(mine, theirs)
            : null;

    public bool HasFingerprint => Fingerprint is { Length: > 0 };

    [ObservableProperty] public partial bool ShowFingerprint { get; set; }

    [RelayCommand]
    private void ToggleFingerprint() => ShowFingerprint = !ShowFingerprint;

    /// <summary>
    /// Everything unread, across every conversation, for the badge on the friends button.
    ///
    /// The drawer is shut most of the time, so without this a message arriving is a thing that
    /// happened silently behind a button with nothing on it.
    /// </summary>
    public int TotalUnread => Friends.Sum(f => f.Unread);

    public bool HasAnyUnread => TotalUnread > 0;
    public string TotalUnreadLabel => TotalUnread > 9 ? "9+" : TotalUnread.ToString();

    /// <summary>Called wherever a count moves, since the total is derived from all of them.</summary>
    private void RaiseUnread()
    {
        OnPropertyChanged(nameof(TotalUnread));
        OnPropertyChanged(nameof(HasAnyUnread));
        OnPropertyChanged(nameof(TotalUnreadLabel));
    }

    /// <summary>
    /// Which folder this account's history lives in. The network identity where there is one, so
    /// that two offline accounts with the same name on one computer do not share a history — they
    /// are different people, whatever they called themselves.
    /// </summary>
    private string ArchiveKey => _accounts.Active is { } account
        ? account.NetworkUuid is { Length: > 0 } network ? network : account.Uuid
        : "none";

    /// <summary>
    /// The conversation with someone, read back off the disk the first time it is asked for.
    ///
    /// Read once and then held: the collection is what the screen is bound to, and re-reading it
    /// on every open would throw away the lines that arrived while it was closed.
    /// </summary>
    private ObservableCollection<ChatLine> ConversationFor(string uuid)
    {
        if (_conversations.TryGetValue(uuid, out var existing)) return existing;

        var conversation = new ObservableCollection<ChatLine>();
        foreach (var line in _archive.Read(ArchiveKey, uuid)) conversation.Add(Restore(line));

        return _conversations[uuid] = conversation;
    }

    /// <summary>
    /// Applies the week and the half-gigabyte, off the UI thread and once per session.
    ///
    /// Once is enough: the only thing that grows the archive is this launcher writing to it, and
    /// a week's worth of somebody's chatting is not going to cross half a gigabyte between one
    /// sign-in and the next. The key is read here rather than inside, because by the time the
    /// work runs the account may have been switched underneath it.
    /// </summary>
    private void PruneArchive()
    {
        var key = ArchiveKey;
        _ = Task.Run(() => _archive.Prune(key));
    }

    /// <summary>One archived line as something the screen can show.</summary>
    private ChatLine Restore(ArchivedLine line) =>
        line.Picture is { Length: > 0 } name && _archive.PicturePath(ArchiveKey, name) is { } path
            ? ChatLine.ForImageFile(path, line.Mine, line.At)
            : new ChatLine(line.Text ?? "", line.Mine, line.At);

    [RelayCommand]
    private void OpenChat(FriendRow? row)
    {
        if (row is null) return;

        ChatError = null;
        Draft = "";
        Disarm();
        ChatWith = row;
        Conversation = ConversationFor(row.Uuid);

        // Reading them is what marks them read.
        row.Unread = 0;
        RaiseUnread();

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
            // Only this launcher can. A message that will not open is shown as one that could
            // not be read rather than hidden: silently dropping it would leave one person
            // wondering why the other never replied.
            ConversationFor(message.From).Add(Unseal(message));

            // Being looked at means the drawer is open *and* this is the conversation in it.
            // Without the first half, closing the drawer mid-conversation left ChatWith pointing
            // at somebody — so everything they said afterwards counted as already read, and the
            // badge that exists to announce it never appeared.
            if (IsOnScreen && string.Equals(ChatWith?.Uuid, message.From, StringComparison.OrdinalIgnoreCase))
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

        RaiseUnread();
    }

    /// <summary>
    /// Asks for a picture and sends it. Set by the view, which owns the window a file dialog
    /// has to hang off.
    /// </summary>
    public Func<Task<string?>>? AskForPicture { get; set; }

    [ObservableProperty] public partial bool IsSendingPicture { get; set; }

    [RelayCommand]
    private async Task SendPictureAsync()
    {
        if (ChatWith is not { } friend || AskForPicture is null || IsSendingPicture) return;

        if (_chatKey is null || friend.PublicKey is not { Length: > 0 } theirs)
        {
            ChatError = $"{friend.Name} is on a version of Asobu that can't receive encrypted messages yet.";
            return;
        }

        var path = await AskForPicture();
        if (path is null) return;

        IsSendingPicture = true;
        ChatError = null;

        try
        {
            // Shrunk and re-encoded off the UI thread. A phone photo is eight megabytes and
            // several seconds of work, and the window should not stop for either.
            var jpeg = await Task.Run(() => ChatImage.Prepare(path));

            if (jpeg is null)
            {
                ChatError = "That file couldn't be read as a picture, or wouldn't shrink small enough to send.";
                return;
            }

            var box = MessageCrypto.Seal(_chatKey, theirs, ChatPayload.OfImage(jpeg));

            var sentAt = DateTimeOffset.UtcNow;
        ConversationFor(friend.Uuid).Add(ChatLine.ForImage(jpeg, mine: true, sentAt));
        _archive.AppendPicture(ArchiveKey, friend.Uuid, sentAt, mine: true, jpeg);
            OnPropertyChanged(nameof(ConversationIsEmpty));

            await _launcher.Friends.SayAsync(friend.Uuid, box);
        }
        catch (Exception e)
        {
            ChatError = e.Message;
        }
        finally
        {
            IsSendingPicture = false;
        }
    }

    [RelayCommand]
    private async Task SendChatAsync()
    {
        if (ChatWith is not { } friend) return;

        // The last place the text exists in the clear, so the last place anything about it can
        // be checked. Control characters go here rather than at the server, which from now on
        // sees only ciphertext.
        var text = Clean(Draft);
        if (text.Length == 0) return;

        if (text.Length > MaxMessageLength) text = text[..MaxMessageLength];

        if (_chatKey is null || friend.PublicKey is not { Length: > 0 } theirs)
        {
            // No fallback to sending it readable. A feature that says it is encrypted and
            // quietly is not, when the other end is old or a key has not arrived, is worse than
            // one that says it cannot send.
            ChatError = $"{friend.Name} is on a version of Asobu that can't receive encrypted messages yet.";
            return;
        }

        string box;
        try
        {
            box = MessageCrypto.Seal(_chatKey, theirs, text);
        }
        catch (Exception e)
        {
            ChatError = "Couldn't encrypt that: " + e.Message;
            return;
        }

        // On screen before it is on the wire. Chat that waits for a round trip before showing
        // what you typed feels broken on a slow connection, and this one cannot be un-said
        // anyway — the server has no record to correct.
        var saidAt = DateTimeOffset.UtcNow;
        ConversationFor(friend.Uuid).Add(new ChatLine(text, mine: true, saidAt));
        _archive.Append(ArchiveKey, friend.Uuid, saidAt, mine: true, text);
        Draft = "";
        ChatError = null;
        OnPropertyChanged(nameof(ConversationIsEmpty));

        try
        {
            await _launcher.Friends.SayAsync(friend.Uuid, box);
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

    /// <summary>Raises the three things that decide which of the page's openings is showing.</summary>
    private void RaiseGates()
    {
        OnPropertyChanged(nameof(NeedsAccount));
        OnPropertyChanged(nameof(NeedsOfflineJoin));
        OnPropertyChanged(nameof(MyHandle));
        OnPropertyChanged(nameof(ShowsHandle));
    }

    /// <summary>
    /// Puts this offline account on the network, which is the one thing here somebody has to ask
    /// for rather than have happen to them.
    ///
    /// The tag comes back from the server rather than being chosen here — that is what makes it
    /// worth anything, since a name anybody can type is not an identity but a name plus four
    /// digits nobody chose is at least a distinct one.
    /// </summary>
    [RelayCommand]
    private async Task JoinNetworkAsync()
    {
        if (_accounts.Active is not { Kind: AccountKind.Offline } account || IsConnecting) return;

        IsConnecting = true;
        ConnectionError = null;

        try
        {
            var identity = await _launcher.Friends.JoinOfflineAsync(
                account, MachineId.ForNetwork(_launcher.Paths));

            account.NetworkUuid = identity.Uuid;
            account.NetworkTag = identity.Tag;
            _accounts.SaveAccounts();
            _accounts.RefreshLabels();

            RaiseGates();

            IsConnected = true;
            StartWatching();
            StartHosting();
            await EnsureChatKeyAsync();
            PruneArchive();

            Notice = $"You're on the network as {identity.Handle}. That's what friends type to find you.";
            NoticeIsGood = true;

            await RefreshAsync(quiet: false);
        }
        catch (FriendsException e)
        {
            // The ceilings answer through here, and they explain themselves — five to a machine,
            // five to a connection — so the server's own words are the right ones to show.
            ConnectionError = e.Message;
        }
        catch (Exception)
        {
            ConnectionError = "Couldn't reach the network. Try again in a moment.";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand]
    private void GoAccounts() => _goAccounts();

    [RelayCommand]
    private Task Retry() => EnsureConnectedAsync(thenRefresh: true);

    private async Task EnsureConnectedAsync(bool thenRefresh)
    {
        RaiseGates();
        if (NeedsAccount || NeedsOfflineJoin || IsConnecting) return;

        if (!IsConnected)
        {
            IsConnecting = true;
            ConnectionError = null;
            try
            {
                var account = _accounts.Active!;
                if (!await _launcher.Friends.TryResumeAsync(account))
                {
                    if (account.Kind == AccountKind.Offline)
                    {
                        // Already on the network — this is the same account coming back after its
                        // stored session aged out, which the server recognises by its own id and
                        // this machine, and hands back rather than counting again.
                        var identity = await _launcher.Friends.JoinOfflineAsync(
                            account, MachineId.ForNetwork(_launcher.Paths));

                        account.NetworkUuid = identity.Uuid;
                        account.NetworkTag = identity.Tag;
                        _accounts.SaveAccounts();
            _accounts.RefreshLabels();
                        RaiseGates();
                    }
                    else
                    {
                        // The full handshake. The Minecraft token this refreshes goes to Mojang
                        // and nowhere else.
                        var session = await _launcher.ResolveSessionAsync(account);
                        await _launcher.Friends.ConnectAsync(session);
                    }
                }
                IsConnected = true;
                StartWatching();
                StartHosting();
            StartHosting();
                await EnsureChatKeyAsync();
                PruneArchive();
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
        if (signature != _shown)
        {
            _shown = signature;

            Rebuild(Friends, friends);
            Rebuild(Incoming, snapshot.Incoming);
            Rebuild(Outgoing, snapshot.Outgoing);
            RaiseCounts();
        }

        // Worlds are refreshed on every snapshot, not only when the rows are rebuilt. A player
        // count ticking over is exactly the sort of thing that changes every few seconds, and
        // rebuilding the list for it would yank rows out from under whoever was reaching for one.
        var worlds = friends.ToDictionary(f => f.Uuid, f => f.World, StringComparer.OrdinalIgnoreCase);
        foreach (var row in Friends) row.World = worlds.GetValueOrDefault(row.Uuid);
    }

    private void Rebuild(ObservableCollection<FriendRow> rows, IEnumerable<Friend> people)
    {
        // What each row was carrying before it was thrown away. A row is rebuilt whenever anyone
        // comes online, and without this every unread badge would vanish the moment a friend's
        // presence changed — including the badge belonging to the message that caused it.
        var unread = rows.ToDictionary(row => row.Uuid, row => row.Unread, StringComparer.OrdinalIgnoreCase);
        var invited = rows.Where(row => row.IsInvited).Select(row => row.Uuid).ToHashSet(StringComparer.OrdinalIgnoreCase);

        rows.Clear();
        foreach (var person in people)
        {
            var row = new FriendRow(person)
            {
                Unread = unread.GetValueOrDefault(person.Uuid),
                IsInvited = invited.Contains(person.Uuid),
            };
            rows.Add(row);
            _ = LoadFaceAsync(row);
        }

        RaiseUnread();
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

    /// <summary>
    /// Unfriending, which asks first.
    ///
    /// The X used to do it on one press, and one press is easy to make by accident in a row of
    /// small buttons — losing somebody who then has to be asked back and has to agree. So the
    /// first press arms and the second means it, and an arming that goes unanswered forgets
    /// itself rather than lying in wait for a stray click later.
    /// </summary>
    [RelayCommand]
    private async Task RemoveFriendAsync(FriendRow? row)
    {
        if (row is null) return;

        if (!row.IsConfirmingRemove)
        {
            foreach (var other in Friends) other.IsConfirmingRemove = ReferenceEquals(other, row);

            // Forgets itself. Left armed, the next stray click on that spot would remove them
            // after all, which is the accident this exists to stop rather than a shorter path
            // to it.
            var armed = row;
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
                Dispatcher.UIThread.Post(() => armed.IsConfirmingRemove = false));

            return;
        }

        row.IsConfirmingRemove = false;
        await RemoveAsync(row);
    }

    /// <summary>Clears any half-pressed X, for leaving the list or changing what it shows.</summary>
    private void Disarm()
    {
        foreach (var row in Friends) row.IsConfirmingRemove = false;
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
