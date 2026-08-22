using System.Net;
using System.Text.Json;
using Asobu.Core.Accounts;

namespace Asobu.Core.Tests;

/// <summary>
/// The stand-in session server that lets a friend without a Microsoft account into a world.
///
/// A service whose job is to vouch for people is only as good as its refusals, so most of what is
/// checked here is what it declines to answer for.
/// </summary>
public class SessionShimTests : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly Upstream _mojang = new();
    private readonly SessionShim _shim;

    public SessionShimTests()
    {
        _shim = new SessionShim(_http, _mojang.Url, _mojang.Url);
        Assert.True(_shim.TryStart(), "the shim could not open a loopback port");
    }

    public void Dispose()
    {
        _shim.Dispose();
        _mojang.Dispose();
        _http.Dispose();
    }

    private Task<HttpResponseMessage> AskAsync(string path) => _http.GetAsync(_shim.BaseUrl + path);

    // ---- The uuid ----

    /// <summary>
    /// The id an offline server would have given that name, so somebody's inventory is found again
    /// next time rather than a fresh player each visit. Values computed independently rather than
    /// copied out of this implementation.
    /// </summary>
    [Theory]
    [InlineData("Dev", "380df991f603344ca090369bad2a924a")]
    [InlineData("Slavky", "fce5eb89edbc381cbc6b95cd5938e6e9")]
    [InlineData("Notch", "b50ad385829d3141a2167e7d7539ba7f")]
    public void The_offline_uuid_matches_what_a_server_would_have_picked(string name, string expected) =>
        Assert.Equal(expected, SessionShim.OfflineUuid(name));

    // ---- Vouching ----

    [Fact]
    public async Task An_invited_guest_is_vouched_for()
    {
        _shim.Vouch("Dev", SessionShim.OfflineUuid("Dev"));

        var answer = await AskAsync("/session/minecraft/hasJoined?username=Dev&serverId=abc");
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);

        using var profile = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
        Assert.Equal("Dev", profile.RootElement.GetProperty("name").GetString());
        Assert.Equal(SessionShim.OfflineUuid("Dev"), profile.RootElement.GetProperty("id").GetString());

        // Never asked anybody else about them.
        Assert.Empty(_mojang.Asked);
    }

    /// <summary>
    /// The whole safety property: a name nobody invited gets whatever the real session server says
    /// about it, which for a stranger is "no such player".
    /// </summary>
    [Fact]
    public async Task A_name_nobody_invited_is_passed_on_rather_than_vouched_for()
    {
        _shim.Vouch("Dev", SessionShim.OfflineUuid("Dev"));

        var answer = await AskAsync("/session/minecraft/hasJoined?username=Stranger&serverId=abc");

        Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode);
        Assert.Contains(_mojang.Asked, path => path.Contains("username=Stranger", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Withdrawing_the_invite_withdraws_the_vouching()
    {
        _shim.Vouch("Dev", SessionShim.OfflineUuid("Dev"));
        _shim.StopVouching("Dev");

        var answer = await AskAsync("/session/minecraft/hasJoined?username=Dev&serverId=abc");

        Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode);
        Assert.NotEmpty(_mojang.Asked);
    }

    [Fact]
    public async Task Closing_the_world_withdraws_all_of_it()
    {
        _shim.Vouch("Dev", SessionShim.OfflineUuid("Dev"));
        _shim.Vouch("Someone", SessionShim.OfflineUuid("Someone"));
        _shim.StopVouchingForEveryone();

        Assert.Equal(HttpStatusCode.NoContent, (await AskAsync("/session/minecraft/hasJoined?username=Dev&serverId=a")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await AskAsync("/session/minecraft/hasJoined?username=Someone&serverId=a")).StatusCode);
    }

    // ---- Joining ----

    /// <summary>
    /// A real account still asks Mojang. Answering "yes" for everybody would quietly stop this
    /// launcher's accounts from being real ones anywhere else.
    /// </summary>
    [Fact]
    public async Task A_real_account_still_asks_mojang_to_join()
    {
        _shim.JoinsWithoutMojang = false;

        var answer = await _http.PostAsync(_shim.BaseUrl + "/session/minecraft/join", new StringContent("{}"));

        Assert.Contains(_mojang.Asked, path => path.EndsWith("/session/minecraft/join", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode);
    }

    [Fact]
    public async Task An_offline_account_is_let_through_without_asking()
    {
        _shim.JoinsWithoutMojang = true;

        var answer = await _http.PostAsync(_shim.BaseUrl + "/session/minecraft/join", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode);
        Assert.Empty(_mojang.Asked);
    }

    // ---- Everything else ----

    /// <summary>
    /// Skins, capes and chat signing keys have to keep working, or pointing the game at this would
    /// cost more than it bought.
    /// </summary>
    [Fact]
    public async Task Anything_else_is_forwarded_untouched()
    {
        await AskAsync("/session/minecraft/profile/abc123");
        await AskAsync("/player/certificates");

        Assert.Contains(_mojang.Asked, path => path.Contains("/profile/abc123", StringComparison.Ordinal));
        Assert.Contains(_mojang.Asked, path => path.Contains("/player/certificates", StringComparison.Ordinal));
    }

    /// <summary>Stands in for Mojang, and remembers what it was asked.</summary>
    private sealed class Upstream : IDisposable
    {
        private HttpListener? _listener;
        public readonly List<string> Asked = [];

        public Upstream()
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var port = Random.Shared.Next(20000, 60000);

                // A fresh one each attempt — a listener whose Start threw is disposed.
                var listener = new HttpListener();
                try
                {
                    listener.Prefixes.Add($"http://localhost:{port}/");
                    listener.Start();
                    _listener = listener;
                    Url = $"http://localhost:{port}";
                    break;
                }
                catch (HttpListenerException)
                {
                    listener.Close();
                }
            }

            _ = Task.Run(async () =>
            {
                while (_listener is { IsListening: true })
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch (Exception) { return; }

                    lock (Asked) Asked.Add(context.Request.Url?.PathAndQuery ?? "");

                    // 204 is what the real one says about a player it has never heard of.
                    context.Response.StatusCode = 204;
                    context.Response.Close();
                }
            });
        }

        public string Url { get; } = "";

        public void Dispose()
        {
            try { _listener?.Close(); } catch (Exception) { }
        }
    }
}
