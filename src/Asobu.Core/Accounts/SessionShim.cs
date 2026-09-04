using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Asobu.Core.Accounts;

/// <summary>
/// The real services this stands in front of. Four rather than one, because the game is told about
/// each separately and each has its own answers.
/// </summary>
public sealed record SessionUpstreams(
    string Auth = "https://authserver.mojang.com",
    string Account = "https://api.mojang.com",
    string Session = "https://sessionserver.mojang.com",
    string Services = "https://api.minecraftservices.com");

/// <summary>
/// A stand-in for Mojang's session server, listening on this machine only.
///
/// It exists so that a friend without a Microsoft account can be let into a world. A world opened
/// to LAN always demands authentication — <c>IntegratedServer.initServer</c> calls
/// <c>setUsesAuthentication(true)</c> before anything else, in every version from 1.8.9 to 1.21.8,
/// vanilla and Forge alike — so the guest's client asks Mojang to vouch for it, Mojang has never
/// heard of it, and the join dies as "Invalid session".
///
/// The alternative was patching the game: a Java agent flipping that flag, which means finding an
/// obfuscated method whose name changes every version and again under every mod loader. This needs
/// none of that. authlib reads its endpoints from <c>minecraft.api.*.host</c> system properties —
/// documented, unobfuscated, honoured the same by vanilla and Forge — so the game can simply be
/// told where to ask, and this answers.
///
/// <para>
/// One port per service, rather than one port and a table deciding which upstream each path
/// belongs to. authlib wants a URL per service anyway, and a port that means exactly one upstream
/// cannot mis-route a path nobody thought of — which, for an API this size, is most of them.
/// </para>
///
/// <para>
/// What it will and will not say, because a service that vouches for people is only as good as its
/// refusals:
/// </para>
/// <list type="bullet">
/// <item>It vouches for a name only while that name holds an invite the host signed. Nothing else.</item>
/// <item>Everything it does not answer itself is forwarded unchanged, so the same instance can
/// still join a real server with a real account.</item>
/// <item>It listens on the loopback address, so it is not reachable from another machine.</item>
/// </list>
/// </summary>
public sealed class SessionShim(HttpClient http, SessionUpstreams? upstreams = null) : IDisposable
{
    private readonly SessionUpstreams _upstreams = upstreams ?? new SessionUpstreams();
    private readonly Dictionary<string, string> _vouched = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which real service a request arriving on this port is meant for.</summary>
    private readonly Dictionary<int, string> _routes = [];

    private HttpListener? _listener;
    private CancellationTokenSource? _stop;

    public string AuthHost { get; private set; } = "";
    public string AccountHost { get; private set; } = "";
    public string SessionHost { get; private set; } = "";
    public string ServicesHost { get; private set; } = "";

    public bool IsRunning => SessionHost.Length > 0;

    /// <summary>
    /// Whether this launcher's own account may join a server without Mojang's blessing. Set for an
    /// offline account, which has no blessing to get — the guest half of the same problem.
    /// </summary>
    public bool JoinsWithoutMojang { get; set; }

    /// <summary>What Mojang said about each vouched uuid, so it is asked once rather than per join.</summary>
    private readonly Dictionary<string, string?> _profiles = [];

    /// <summary>Let this name in for as long as their invite stands.</summary>
    public void Vouch(string username, string uuid)
    {
        lock (_vouched) _vouched[username] = uuid.Replace("-", "", StringComparison.Ordinal);
    }

    public void StopVouching(string username)
    {
        lock (_vouched) _vouched.Remove(username);
    }

    public void StopVouchingForEveryone()
    {
        lock (_vouched) _vouched.Clear();
        lock (_profiles) _profiles.Clear();
    }

    /// <summary>
    /// Opens one loopback port per service. False when the machine will not allow it — the caller
    /// then launches without the properties, and offline guests cannot join, which is the
    /// behaviour there was before any of this.
    /// </summary>
    public bool TryStart()
    {
        if (IsRunning) return true;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            // A fresh listener each attempt: one whose Start threw is disposed, and reusing it
            // turns a busy port into an ObjectDisposedException on the next try.
            var listener = new HttpListener();
            var first = Random.Shared.Next(20000, 60000);

            try
            {
                // localhost rather than a wildcard: HttpListener wants an administrator-registered
                // reservation for anything else on Windows, and a launcher should not want one.
                for (var offset = 0; offset < 4; offset++)
                    listener.Prefixes.Add($"http://localhost:{first + offset}/");

                listener.Start();

                _routes[first] = _upstreams.Auth;
                _routes[first + 1] = _upstreams.Account;
                _routes[first + 2] = _upstreams.Session;
                _routes[first + 3] = _upstreams.Services;

                AuthHost = $"http://localhost:{first}";
                AccountHost = $"http://localhost:{first + 1}";
                SessionHost = $"http://localhost:{first + 2}";
                ServicesHost = $"http://localhost:{first + 3}";

                _listener = listener;
                _stop = new CancellationTokenSource();
                _ = ServeAsync(_stop.Token);
                return true;
            }
            catch (HttpListenerException)
            {
                // One of those four was taken. Another block costs nothing to try.
                listener.Close();
                _routes.Clear();
            }
        }

        return false;
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener!.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = AnswerAsync(context, cancellationToken);
        }
    }

    private async Task AnswerAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "";
            var upstream = _routes.GetValueOrDefault(context.Request.LocalEndPoint.Port, _upstreams.Services);

            // The client asking permission to join. An account Mojang knows gets the real answer;
            // an offline one is told yes, because there is nobody to ask on its behalf.
            if (path.EndsWith("/session/minecraft/join", StringComparison.OrdinalIgnoreCase) && JoinsWithoutMojang)
            {
                context.Response.StatusCode = 204;
                context.Response.Close();
                return;
            }

            // The server asking who just arrived. This is the question worth answering ourselves.
            if (path.EndsWith("/session/minecraft/hasJoined", StringComparison.OrdinalIgnoreCase))
            {
                var name = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? "")["username"];
                if (name is { Length: > 0 } && Vouching(name) is { } uuid)
                {
                    // Their real profile if Mojang has one, so a vouched player keeps their skin.
                    // Answering with a bare id and name says "this is who they are" and nothing
                    // else, and a profile with no textures is how everybody ends up as Steve.
                    //
                    // Only worth asking for an id that could belong to an account. A guest whose
                    // id we worked out from their name ourselves is one Mojang has never heard
                    // of, and asking about them is a request that can only come back empty.
                    var real = uuid == OfflineUuid(name)
                        ? null
                        : await RealProfileAsync(uuid, cancellationToken).ConfigureAwait(false);

                    await WriteJsonAsync(context, real ?? Profile(name, uuid)).ConfigureAwait(false);
                    return;
                }
            }

            // Skins, capes, chat signing keys, real sign-ins: none of our business.
            await ForwardAsync(context, upstream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try
            {
                context.Response.StatusCode = 502;
                context.Response.Close();
            }
            catch (Exception)
            {
                // The caller hung up while we were deciding. Nothing left to answer.
            }
        }
    }

    /// <summary>
    /// What Mojang says about a uuid, signature and all, or null for one it has never heard of.
    ///
    /// Kept once fetched. A server asks about a player every time they join, and the answer only
    /// changes when they change their skin — which is not often enough to ask Mojang about on
    /// every doorstep.
    /// </summary>
    private async Task<string?> RealProfileAsync(string uuid, CancellationToken cancellationToken)
    {
        lock (_profiles)
            if (_profiles.TryGetValue(uuid, out var remembered))
                return remembered;

        string? profile = null;

        try
        {
            var url = $"{_upstreams.Session}/session/minecraft/profile/{uuid}?unsigned=false";
            using var answer = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (answer.IsSuccessStatusCode)
            {
                var body = await answer.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                // An offline uuid gets an empty body rather than a refusal, and a profile with no
                // properties is no better than the one we would have written ourselves.
                if (body.Contains("\"properties\"", StringComparison.Ordinal)
                    && body.Contains("\"textures\"", StringComparison.Ordinal))
                {
                    profile = body;
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // Offline, or Mojang having a moment. The plain profile still lets them in.
        }

        lock (_profiles) _profiles[uuid] = profile;

        return profile;
    }

    private string? Vouching(string username)
    {
        lock (_vouched) return _vouched.GetValueOrDefault(username);
    }

    /// <summary>
    /// The profile the server is told about, in the shape Mojang would have sent. The id is the
    /// one an offline server would have assigned to that name, so somebody's inventory is found
    /// again next time rather than being handed a new player each visit.
    /// </summary>
    internal static string Profile(string username, string uuid) =>
        JsonSerializer.Serialize(new { id = uuid, name = username, properties = Array.Empty<object>() });

    /// <summary>
    /// The uuid an offline server gives a name: version 3, over "OfflinePlayer:name". Mojang's own
    /// convention, reimplemented here because nothing in the framework spells it.
    /// </summary>
    public static string OfflineUuid(string username)
    {
        var digest = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));

        digest[6] = (byte)((digest[6] & 0x0F) | 0x30);   // version 3
        digest[8] = (byte)((digest[8] & 0x3F) | 0x80);   // RFC 4122 variant

        return Convert.ToHexStringLower(digest);
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);

        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;

        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    /// <summary>Hands the request on to the real service and copies the answer back, unread.</summary>
    private async Task ForwardAsync(HttpListenerContext context, string host, CancellationToken cancellationToken)
    {
        var request = context.Request;
        using var forwarded = new HttpRequestMessage(
            new HttpMethod(request.HttpMethod),
            host + (request.Url?.PathAndQuery ?? "/"));

        if (request.HasEntityBody)
        {
            using var body = new MemoryStream();
            await request.InputStream.CopyToAsync(body, cancellationToken).ConfigureAwait(false);

            forwarded.Content = new ByteArrayContent(body.ToArray());
            if (request.ContentType is { Length: > 0 } type)
                forwarded.Content.Headers.TryAddWithoutValidation("Content-Type", type);
        }

        if (request.Headers["Authorization"] is { Length: > 0 } authorization)
            forwarded.Headers.TryAddWithoutValidation("Authorization", authorization);

        using var answer = await http.SendAsync(forwarded, cancellationToken).ConfigureAwait(false);
        var payload = await answer.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        context.Response.StatusCode = (int)answer.StatusCode;
        if (answer.Content.Headers.ContentType is { } contentType)
            context.Response.ContentType = contentType.ToString();

        context.Response.ContentLength64 = payload.Length;
        if (payload.Length > 0)
            await context.Response.OutputStream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);

        context.Response.Close();
    }

    public void Dispose()
    {
        _stop?.Cancel();
        _stop?.Dispose();

        try { _listener?.Close(); }
        catch (Exception) { /* Already shut, which is where we were going. */ }
    }
}
