using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asobu.Core.Accounts;
using Asobu.Core.Hosting;

namespace Asobu.Core.Online;

/// <summary>Someone on the network, as the friends list shows them.</summary>
public sealed record Friend(string Uuid, string Name, bool Online, DateTimeOffset LastSeen)
{
    /// <summary>
    /// The public half of their chat key, or null when they have not published one — an older
    /// launcher, or one still starting. Without it there is no way to write to them that they
    /// alone could read, so the answer is to say so rather than to send something readable.
    /// </summary>
    public string? PublicKey { get; init; }

    /// <summary>
    /// Four digits for an offline account, empty for one Mojang vouches for. Shown wherever the
    /// name is, so that a name carrying no proof never sits beside one that does looking exactly
    /// the same — and so the pair can be typed back in to find them.
    /// </summary>
    public string? Tag { get; init; }

    public bool IsOffline => Tag is { Length: > 0 };

    /// <summary>What to show, and what somebody would type to find them.</summary>
    public string Handle => IsOffline ? $"{Name}#{Tag}" : Name;

    /// <summary>The world they have open, or null. Most friends, most of the time, have none.</summary>
    public FriendWorld? World { get; init; }
}

/// <summary>
/// A friend's open world, seen from outside.
///
/// Everyone on their list sees the name and how busy it is. <see cref="Addresses"/> and
/// <see cref="Pass"/> arrive only for somebody who was invited, so an uninvited friend sees the
/// world happening and nothing they could use to turn up at it.
/// </summary>
public sealed record FriendWorld(string Name, int Players, int Max)
{
    /// <summary>
    /// What the world reports itself as — "1.21.8", or whatever a modded server calls itself.
    /// Used to grey out instances that could not join it. Null from a host who could not be
    /// asked, in which case nothing is greyed rather than everything.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Stands for the world's version, loader and mod list together. A friend holding an instance
    /// with the same one can join straight away instead of being asked which to use.
    /// </summary>
    public string? Fingerprint { get; init; }

    /// <summary>Where the host's door might be, cheapest first: their network, then the internet.</summary>
    public IReadOnlyList<string> Addresses { get; init; } = [];

    /// <summary>The pass their host signed for me, meaningless to anyone else.</summary>
    public string? Pass { get; init; }

    public bool AmInvited => Pass is { Length: > 0 } && Addresses.Count > 0;

    /// <summary>"2/8", or just the count when the world never said what its limit was.</summary>
    public string Busy => Max > 0 ? $"{Players}/{Max}" : Players.ToString();
}

/// <summary>What the network calls an offline account once it has let one in.</summary>
public sealed record OfflineIdentity(string Uuid, string Tag, string Handle);

/// <summary>
/// One thing a friend said, as it arrived.
///
/// The server relays chat and keeps none of it, so this is the only copy there will ever be —
/// once a snapshot carrying it has been read, asking again returns nothing.
/// </summary>
public sealed record ChatMessage(string From, string Name, string Box, DateTimeOffset At);

/// <summary>The whole social picture in one answer: friends, requests waiting on you, requests waiting on them.</summary>
public sealed record FriendsSnapshot(
    IReadOnlyList<Friend> Friends,
    IReadOnlyList<Friend> Incoming,
    IReadOnlyList<Friend> Outgoing,
    long Revision = 0,
    IReadOnlyList<ChatMessage>? Messages = null)
{
    /// <summary>
    /// Anything said since the last answer. Carried on the friends snapshot rather than fetched
    /// separately: the launcher already holds one request open, and chat rides it.
    ///
    /// Handed over once. Whatever reads a snapshot owns these — dropping them loses them.
    /// </summary>
    public IReadOnlyList<ChatMessage> Messages { get; init; } = Messages ?? [];

    public static readonly FriendsSnapshot Empty = new([], [], []);
}

/// <summary>A refusal the server explained; the message is fit to show as-is.</summary>
public class FriendsException(string message) : Exception(message);

/// <summary>The stored session is no longer good. Sign in again and retry.</summary>
public sealed class FriendsAuthException() : FriendsException("Not signed in to Asobu.");

/// <summary>
/// Talks to the Asobu API about friends.
///
/// Identity is proved through Mojang rather than by sending tokens to us: the API hands out a
/// random serverId, this client "joins" it against Mojang's session server using the Minecraft
/// token it already holds for launching, and the API asks Mojang who joined. The Microsoft and
/// Minecraft tokens never leave the machine except to Mojang — exactly as they would when
/// entering any multiplayer server.
/// </summary>
public sealed class FriendsClient(HttpClient http, AsobuPaths paths)
{
    private const string Api = "https://api.asobu.cc/v1/";
    private const string SessionJoin = "https://sessionserver.mojang.com/session/minecraft/join";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Same encrypted store the device-code tokens live in; the key scopes it per account.</summary>
    private readonly TokenVault _vault = new(paths);

    private string? _token;

    public bool IsConnected => _token is not null;

    /// <summary>
    /// Picks the session up where it was left, using only the stored Asobu token — no Microsoft
    /// refresh, no Mojang round-trip. Cheap enough for startup; false just means the full
    /// connect is needed.
    /// </summary>
    public async Task<bool> TryResumeAsync(Account account, CancellationToken cancellationToken = default)
    {
        var stored = _vault.Get(VaultKey(account.Uuid));
        if (stored is null) return false;

        _token = stored;
        try
        {
            await GetFriendsAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (FriendsAuthException)
        {
            _vault.Remove(VaultKey(account.Uuid));
            return false;
        }
        catch (Exception)
        {
            // The server being unreachable says nothing about the token. Stay optimistic;
            // a later call will settle it.
            return true;
        }
    }

    /// <summary>
    /// Joins the network as this session's player. Three requests: ask for a serverId, tell
    /// Mojang we joined it, let the API confirm with Mojang and hand back an Asobu session.
    /// </summary>
    public async Task ConnectAsync(MinecraftSession session, CancellationToken cancellationToken = default)
    {
        _token = null;

        var begin = await PostAsync<BeginReply>("auth/begin", new { name = session.Username }, cancellationToken)
            .ConfigureAwait(false);

        // The Minecraft token goes to Mojang and only Mojang, same as joining any server.
        using var join = await http.PostAsJsonAsync(SessionJoin, new
        {
            accessToken = session.AccessToken,
            selectedProfile = session.Uuid.Replace("-", ""),
            serverId = begin.ServerId,
        }, cancellationToken).ConfigureAwait(false);

        if (join.StatusCode != HttpStatusCode.NoContent)
            throw new FriendsException("Mojang rejected the sign-in. Try signing out and back in.");

        var done = await PostAsync<CompleteReply>(
            "auth/complete", new { name = session.Username, serverId = begin.ServerId }, cancellationToken)
            .ConfigureAwait(false);

        _token = done.Token;
        _vault.Set(VaultKey(session.Uuid), done.Token);
    }

    /// <summary>
    /// Puts an offline account on the network, under a name it chose and a tag it did not.
    ///
    /// There is no handshake to do here, because there is nothing an offline account could prove:
    /// it is a name somebody typed. So the server names it instead — four random digits, and the
    /// pair is what friends type to find them. What holds the door is the ceiling on how many one
    /// machine and one connection may bring in, which is why the machine digest goes along.
    ///
    /// The account's own network id is sent when it has one, so a reinstall is recognised as the
    /// same person coming back rather than spending another of their five.
    /// </summary>
    public async Task<OfflineIdentity> JoinOfflineAsync(
        Account account, string machineId, CancellationToken cancellationToken = default)
    {
        _token = null;

        var reply = await PostAsync<OfflineReply>("offline/join", new
        {
            name = account.Username,
            hwid = machineId,
            uuid = account.NetworkUuid ?? "",
        }, cancellationToken).ConfigureAwait(false);

        _token = reply.Token;
        _vault.Set(VaultKey(account.Uuid), reply.Token);

        return new OfflineIdentity(reply.Uuid, reply.Tag, reply.Handle);
    }

    /// <summary>Forgets the session for an account, locally and for good.</summary>
    public void Disconnect(string accountUuid)
    {
        _token = null;
        _vault.Remove(VaultKey(accountUuid));
    }

    /// <summary>Also the heartbeat: the server counts the caller as online whenever this is asked.</summary>
    public async Task<FriendsSnapshot> GetFriendsAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendAsync<SnapshotReply>(HttpMethod.Get, "friends", null, cancellationToken)
            .ConfigureAwait(false);

        return new FriendsSnapshot(
            reply.Friends ?? [], reply.Incoming ?? [], reply.Outgoing ?? [], reply.Revision, reply.Messages);
    }

    /// <summary>
    /// The same list, but the server holds the request until something actually changes.
    ///
    /// This is what makes a friend request appear on the other screen while it is open. Polling
    /// asks over and over to be told nothing has happened and is still slower; this asks once
    /// and is answered the instant there is something to say. A quiet spell ends with an answer
    /// anyway after about twenty seconds, which doubles as the heartbeat that keeps presence
    /// fresh, so nothing else has to run on a timer.
    /// </summary>
    public async Task<FriendsSnapshot> WatchAsync(long since, CancellationToken cancellationToken = default)
    {
        var reply = await SendAsync<SnapshotReply>(
                HttpMethod.Get, $"friends/watch?since={since}", null, cancellationToken)
            .ConfigureAwait(false);

        return new FriendsSnapshot(
            reply.Friends ?? [], reply.Incoming ?? [], reply.Outgoing ?? [], reply.Revision, reply.Messages);
    }

    /// <summary>
    /// Says something to a friend.
    ///
    /// The server passes it on and keeps nothing, so there is no history to fetch and no
    /// conversation to open — sending is the whole of it, and what comes back arrives on the
    /// watch like everything else.
    /// </summary>
    public Task SayAsync(string uuid, string box, CancellationToken cancellationToken = default) =>
        SendAsync<OkReply>(HttpMethod.Post, "chat", new { to = uuid, box }, cancellationToken);

    /// <summary>
    /// Publishes the public half of this launcher's chat key, so friends can seal messages that
    /// only it can open. Sent on every connect: it is cheap, the server ignores an unchanged one,
    /// and a key that never arrived is a conversation nobody can start.
    /// </summary>
    public Task PublishKeyAsync(string publicKey, CancellationToken cancellationToken = default) =>
        SendAsync<OkReply>(HttpMethod.Post, "chat/key", new { publicKey }, cancellationToken);

    /// <summary>
    /// Says a world is open, and keeps saying it. Also the player count, which is the thing that
    /// changes — so there is no separate heartbeat, because one request is fewer than two.
    ///
    /// Stops being true on its own. A launcher that is killed, or a machine that loses its
    /// network, stops calling this and the world drops off everyone's list a minute later
    /// without anybody having to notice.
    /// </summary>
    public Task OpenWorldAsync(
        string name, int players, int max, int port, string? version, string? fingerprint,
        CancellationToken cancellationToken = default) =>
        SendAsync<OkReply>(HttpMethod.Post, "host/open",
            new { name, players, max, port, version, fingerprint, local = LocalAddresses.For(port) },
            cancellationToken);

    public Task CloseWorldAsync(CancellationToken cancellationToken = default) =>
        SendAsync<OkReply>(HttpMethod.Post, "host/close", new { }, cancellationToken);

    /// <summary>
    /// Hands one friend a pass. The server carries it and cannot read it — it was signed by this
    /// machine and will be checked by this machine, so nothing in the middle can make another.
    /// </summary>
    public Task InviteAsync(string uuid, string pass, CancellationToken cancellationToken = default) =>
        SendAsync<OkReply>(HttpMethod.Post, "host/invites", new { uuid, pass }, cancellationToken);

    /// <summary>Shuts the door to somebody. Anyone already inside stays there.</summary>
    public Task UninviteAsync(string uuid, CancellationToken cancellationToken = default) =>
        SendAsync<OkReply>(HttpMethod.Delete, "host/invites/" + uuid, null, cancellationToken);

    public Task AddAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync<OkReply>(HttpMethod.Post, "friends/requests", new { name }, cancellationToken);

    public Task AcceptAsync(string uuid, CancellationToken cancellationToken = default) =>
        SendAsync<OkReply>(HttpMethod.Post, "friends/accept", new { uuid }, cancellationToken);

    /// <summary>Unfriends, cancels an outgoing request, or declines an incoming one — whichever exists.</summary>
    public Task RemoveAsync(string uuid, CancellationToken cancellationToken = default) =>
        SendAsync<OkReply>(HttpMethod.Delete, "friends/" + uuid, null, cancellationToken);

    private static string VaultKey(string accountUuid) => "asobu:" + accountUuid;

    private Task<T> PostAsync<T>(string path, object body, CancellationToken cancellationToken) =>
        SendAsync<T>(HttpMethod.Post, path, body, cancellationToken);

    /// <summary>
    /// One authenticated call to the Asobu API. Internal rather than private so sharing can use
    /// it: a share code is made by the same signed-in person, over the same session, and a
    /// second copy of the bearer handling would be a second place for it to go wrong.
    /// </summary>
    internal async Task<T> SendAsync<T>(
        HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, Api + path);
        if (_token is not null) request.Headers.Authorization = new("Bearer", _token);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new FriendsException("Couldn't reach Asobu. Check your connection and try again.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _token = null;
                throw new FriendsAuthException();
            }

            if (!response.IsSuccessStatusCode)
                throw new FriendsException(await ErrorFrom(response, cancellationToken).ConfigureAwait(false));

            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false)
                   ?? throw new FriendsException("Asobu sent back something unreadable.");
        }
    }

    /// <summary>The server's own explanation where it gave one, a plain fallback where it didn't.</summary>
    private static async Task<string> ErrorFrom(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await response.Content.ReadFromJsonAsync<ErrorReply>(Json, cancellationToken)
                .ConfigureAwait(false);
            if (reply?.Error is { Length: > 0 } explained) return explained;
        }
        catch (Exception e) when (e is JsonException or HttpRequestException)
        {
        }

        return $"Asobu said no ({(int)response.StatusCode}).";
    }

    private sealed record BeginReply(string ServerId);
    private sealed record CompleteReply(string Token, string Uuid, string Name);
    private sealed record OfflineReply(string Token, string Uuid, string Name, string Tag, string Handle);
    private sealed record OkReply(bool Ok);
    private sealed record ErrorReply(string? Error);

    private sealed record SnapshotReply(
        List<Friend>? Friends, List<Friend>? Incoming, List<Friend>? Outgoing, long Revision,
        List<ChatMessage>? Messages);
}
