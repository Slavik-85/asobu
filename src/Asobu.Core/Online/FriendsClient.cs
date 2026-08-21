using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asobu.Core.Accounts;

namespace Asobu.Core.Online;

/// <summary>Someone on the network, as the friends list shows them.</summary>
public sealed record Friend(string Uuid, string Name, bool Online, DateTimeOffset LastSeen);

/// <summary>The whole social picture in one answer: friends, requests waiting on you, requests waiting on them.</summary>
public sealed record FriendsSnapshot(
    IReadOnlyList<Friend> Friends,
    IReadOnlyList<Friend> Incoming,
    IReadOnlyList<Friend> Outgoing)
{
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

        return new FriendsSnapshot(reply.Friends ?? [], reply.Incoming ?? [], reply.Outgoing ?? []);
    }

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
    private sealed record OkReply(bool Ok);
    private sealed record ErrorReply(string? Error);

    private sealed record SnapshotReply(List<Friend>? Friends, List<Friend>? Incoming, List<Friend>? Outgoing);
}
