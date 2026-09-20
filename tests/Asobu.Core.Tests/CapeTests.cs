using System.Net;
using System.Net.Http.Headers;
using Asobu.Core.Accounts;
using Asobu.Core.Skins;

namespace Asobu.Core.Tests;

/// <summary>
/// Capes, which are not skins.
///
/// A skin is a file anybody can upload; a cape is something Mojang granted to an account, and the
/// account can only choose between the ones it has. So the whole feature is reading what the
/// profile says is owned, and pointing at one of them — and what is tested here is that reading,
/// against the shape Mojang's profile endpoint actually returns.
/// </summary>
public class CapeTests
{
    /// <summary>The account's own profile, as the services API returns it, cut to what matters.</summary>
    private const string Profile = """
        {
          "id": "165009cf3a0e4b0e9f1a1a1a1a1a1a1a",
          "name": "Slavky",
          "skins": [ { "id": "s1", "state": "ACTIVE", "url": "http://textures.minecraft.net/texture/aaa", "variant": "CLASSIC" } ],
          "capes": [
            { "id": "cape-migrator", "state": "INACTIVE", "url": "http://textures.minecraft.net/texture/mig", "alias": "Migrator" },
            { "id": "cape-vanilla",  "state": "ACTIVE",   "url": "http://textures.minecraft.net/texture/van", "alias": "Vanilla" },
            { "id": "cape-nameless", "state": "INACTIVE", "url": "http://textures.minecraft.net/texture/nam" }
          ]
        }
        """;

    /// <summary>Answers every request with one canned reply, and remembers what was asked.</summary>
    private sealed class Canned(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Asked { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked = request;
            Body = request.Content is { } content ? await content.ReadAsStringAsync(cancellationToken) : null;

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private static MinecraftSession Microsoft() => new("Slavky", "165009cf3a0e4b0e9f1a1a1a1a1a1a1a", "tok", "msa", null);
    private static MinecraftSession Offline() => new("Guest", "0123456789abcdef0123456789abcdef", "0", "legacy", null);

    // ---- reading what is owned ----

    [Fact]
    public async Task Every_cape_on_the_profile_comes_back()
    {
        var handler = new Canned(HttpStatusCode.OK, Profile);
        var service = new SkinService(new HttpClient(handler));

        var capes = await service.CapesAsync(Microsoft());

        Assert.Equal(3, capes.Count);
        Assert.Equal(["cape-migrator", "cape-vanilla", "cape-nameless"], capes.Select(c => c.Id));
    }

    /// <summary>The one being worn is the one whose state Mojang marks ACTIVE, and only that one.</summary>
    [Fact]
    public async Task The_one_being_worn_is_the_one_marked_active()
    {
        var service = new SkinService(new HttpClient(new Canned(HttpStatusCode.OK, Profile)));

        var capes = await service.CapesAsync(Microsoft());

        Assert.Equal("cape-vanilla", capes.Single(c => c.Active).Id);
    }

    /// <summary>A cape Mojang gave no alias to still needs a name on a card.</summary>
    [Fact]
    public async Task A_cape_with_no_alias_is_still_called_something()
    {
        var service = new SkinService(new HttpClient(new Canned(HttpStatusCode.OK, Profile)));

        var nameless = (await service.CapesAsync(Microsoft())).Single(c => c.Id == "cape-nameless");

        Assert.Equal("Cape", nameless.Alias);
    }

    /// <summary>It is the account's own profile being asked for, with the account's own token.</summary>
    [Fact]
    public async Task It_asks_the_services_profile_with_the_accounts_token()
    {
        var handler = new Canned(HttpStatusCode.OK, Profile);
        await new SkinService(new HttpClient(handler)).CapesAsync(Microsoft());

        Assert.Equal("https://api.minecraftservices.com/minecraft/profile", handler.Asked!.RequestUri!.ToString());
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "tok"), handler.Asked.Headers.Authorization);
    }

    /// <summary>
    /// An offline account has no profile to ask. Answered as owning nothing rather than by asking
    /// Mojang about a token that is not one, which would be a 401 dressed up as an error.
    /// </summary>
    [Fact]
    public async Task An_offline_account_owns_nothing_and_nobody_is_asked()
    {
        var handler = new Canned(HttpStatusCode.OK, Profile);

        var capes = await new SkinService(new HttpClient(handler)).CapesAsync(Offline());

        Assert.Empty(capes);
        Assert.Null(handler.Asked);
    }

    [Fact]
    public async Task A_refused_profile_is_an_error_that_says_so()
    {
        var service = new SkinService(new HttpClient(new Canned(HttpStatusCode.Unauthorized, "")));

        var refused = await Assert.ThrowsAsync<SkinException>(() => service.CapesAsync(Microsoft()));

        Assert.Contains("401", refused.Message);
    }

    // ---- choosing one ----

    /// <summary>The body is exactly what Mojang documents: a JSON object with the cape's id.</summary>
    [Fact]
    public async Task Wearing_one_puts_its_id_to_the_active_cape_endpoint()
    {
        var handler = new Canned(HttpStatusCode.OK, "{}");
        await new SkinService(new HttpClient(handler)).WearCapeAsync(Microsoft(), "cape-migrator");

        Assert.Equal(HttpMethod.Put, handler.Asked!.Method);
        Assert.Equal("https://api.minecraftservices.com/minecraft/profile/capes/active", handler.Asked.RequestUri!.ToString());
        Assert.Contains("\"capeId\":\"cape-migrator\"", handler.Body);
    }

    [Fact]
    public async Task Taking_it_off_deletes_the_active_cape()
    {
        var handler = new Canned(HttpStatusCode.OK, "{}");
        await new SkinService(new HttpClient(handler)).HideCapeAsync(Microsoft());

        Assert.Equal(HttpMethod.Delete, handler.Asked!.Method);
        Assert.Equal("https://api.minecraftservices.com/minecraft/profile/capes/active", handler.Asked.RequestUri!.ToString());
    }

    [Fact]
    public async Task An_offline_account_cannot_wear_one_and_is_told_why()
    {
        var service = new SkinService(new HttpClient(new Canned(HttpStatusCode.OK, "{}")));

        var refused = await Assert.ThrowsAsync<SkinException>(() => service.WearCapeAsync(Offline(), "cape-migrator"));

        Assert.Contains("Offline", refused.Message);
    }
}
