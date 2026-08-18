using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Asobu.Core.Accounts;

public sealed class MicrosoftAuthException(string message) : Exception(message);

/// <summary>
/// Microsoft sign-in, done the way Mojang expects: system browser with PKCE, then the
/// Xbox Live to XSTS to Minecraft services exchange. Asobu never sees a password, and the
/// refresh token lives in the OS credential store (DPAPI on Windows), never in a file we own.
/// </summary>
public sealed class MicrosoftAuth(HttpClient http, AsobuPaths paths, string clientId)
{
    private static readonly string[] Scopes = ["XboxLive.signin", "offline_access"];

    private const string XboxAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsAuthUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string MinecraftLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string MinecraftProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    private IPublicClientApplication? _app;

    public static bool IsConfigured(string? clientId) => clientId is { Length: > 0 };

    /// <summary>Interactive sign-in. Opens the user's own browser; no credentials pass through Asobu.</summary>
    public async Task<(Account Account, MinecraftSession Session)> SignInAsync(CancellationToken cancellationToken = default)
    {
        var app = await GetAppAsync().ConfigureAwait(false);

        var result = await app
            .AcquireTokenInteractive(Scopes)
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        var session = await ExchangeAsync(result.AccessToken, cancellationToken).ConfigureAwait(false);

        var account = new Account
        {
            Uuid = session.Uuid,
            Username = session.Username,
            Kind = AccountKind.Microsoft,
            HomeAccountId = result.Account?.HomeAccountId?.Identifier,
        };

        return (account, session);
    }

    /// <summary>
    /// Refreshes an existing account without prompting. Throws if the cached refresh token is
    /// gone, which is the caller's cue to run <see cref="SignInAsync"/> again.
    /// </summary>
    public async Task<MinecraftSession> GetSessionAsync(Account account, CancellationToken cancellationToken = default)
    {
        var app = await GetAppAsync().ConfigureAwait(false);

        var cached = (await app.GetAccountsAsync().ConfigureAwait(false))
            .FirstOrDefault(a => a.HomeAccountId?.Identifier == account.HomeAccountId);

        if (cached is null)
            throw new MicrosoftAuthException($"{account.Username} needs to sign in to Microsoft again.");

        try
        {
            var result = await app
                .AcquireTokenSilent(Scopes, cached)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            return await ExchangeAsync(result.AccessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (MsalUiRequiredException)
        {
            throw new MicrosoftAuthException($"{account.Username} needs to sign in to Microsoft again.");
        }
    }

    public async Task SignOutAsync(Account account)
    {
        var app = await GetAppAsync().ConfigureAwait(false);
        foreach (var cached in await app.GetAccountsAsync().ConfigureAwait(false))
            if (cached.HomeAccountId?.Identifier == account.HomeAccountId)
                await app.RemoveAsync(cached).ConfigureAwait(false);
    }

    private async Task<IPublicClientApplication> GetAppAsync()
    {
        if (_app is not null) return _app;

        if (!IsConfigured(clientId))
            throw new MicrosoftAuthException(
                "No Microsoft client id is configured. Add an Azure app registration id to settings.json " +
                "as \"microsoftClientId\" — Minecraft sign-in requires one that Mojang has approved.");

        var app = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority(AadAuthorityAudience.PersonalMicrosoftAccount)
            .WithRedirectUri("http://localhost")
            .Build();

        // MSAL's own encrypted cache: DPAPI on Windows, Keychain on macOS, Secret Service on Linux.
        var storage = new StorageCreationPropertiesBuilder("msal.cache", paths.Root)
            .WithMacKeyChain("Asobu", "MicrosoftAccount")
            .WithLinuxKeyring("cc.asobu.tokens", "default", "Asobu Microsoft tokens",
                new KeyValuePair<string, string>("app", "asobu"),
                default)
            .Build();

        var cache = await MsalCacheHelper.CreateAsync(storage).ConfigureAwait(false);
        cache.RegisterCache(app.UserTokenCache);

        return _app = app;
    }

    /// <summary>Microsoft token to Xbox Live to XSTS to a Minecraft session.</summary>
    private async Task<MinecraftSession> ExchangeAsync(string microsoftToken, CancellationToken cancellationToken)
    {
        var xbox = await PostAsync<XboxResponse>(XboxAuthUrl, new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = "d=" + microsoftToken,
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        }, cancellationToken).ConfigureAwait(false);

        var xsts = await PostXstsAsync(xbox.Token, cancellationToken).ConfigureAwait(false);

        var userHash = xsts.DisplayClaims?.Xui?.FirstOrDefault()?.UserHash
            ?? throw new MicrosoftAuthException("Xbox Live returned no user hash.");

        var minecraft = await PostAsync<MinecraftLoginResponse>(MinecraftLoginUrl, new
        {
            identityToken = $"XBL3.0 x={userHash};{xsts.Token}",
        }, cancellationToken).ConfigureAwait(false);

        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, MinecraftProfileUrl);
        profileRequest.Headers.Authorization = new("Bearer", minecraft.AccessToken);

        using var profileResponse = await http.SendAsync(profileRequest, cancellationToken).ConfigureAwait(false);
        if (profileResponse.StatusCode == HttpStatusCode.NotFound)
            throw new MicrosoftAuthException("This Microsoft account does not own Minecraft: Java Edition.");
        profileResponse.EnsureSuccessStatusCode();

        var profile = await profileResponse.Content
            .ReadFromJsonAsync<MinecraftProfile>(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new MicrosoftAuthException("Minecraft returned an empty profile.");

        return new MinecraftSession(
            profile.Name,
            profile.Id,
            minecraft.AccessToken,
            "msa",
            xsts.DisplayClaims?.Xui?.FirstOrDefault()?.Xuid);
    }

    private async Task<XboxResponse> PostXstsAsync(string xboxToken, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(XstsAuthUrl, new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xboxToken } },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT",
        }, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var failure = await response.Content.ReadFromJsonAsync<XstsError>(cancellationToken).ConfigureAwait(false);
            throw new MicrosoftAuthException(failure?.XErr switch
            {
                2148916233 => "This Microsoft account has no Xbox profile. Create one at xbox.com, then try again.",
                2148916235 => "Xbox Live is not available in this account's country.",
                2148916236 or 2148916237 => "This account needs adult verification before it can use Xbox Live.",
                2148916238 => "This is a child account. Add it to a Microsoft family group first.",
                _ => "Xbox Live rejected the sign-in.",
            });
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<XboxResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new MicrosoftAuthException("Xbox Live returned an empty response.");
    }

    private async Task<T> PostAsync<T>(string url, object body, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(url, body, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false)
            ?? throw new MicrosoftAuthException($"Empty response from {url}.");
    }

    private sealed class XboxResponse
    {
        public string Token { get; init; } = "";
        public XboxDisplayClaims? DisplayClaims { get; init; }
    }

    private sealed class XboxDisplayClaims
    {
        public List<XboxUserInfo>? Xui { get; init; }
    }

    private sealed class XboxUserInfo
    {
        [JsonPropertyName("uhs")] public string? UserHash { get; init; }
        [JsonPropertyName("xid")] public string? Xuid { get; init; }
    }

    private sealed class XstsError
    {
        [JsonPropertyName("XErr")] public long XErr { get; init; }
    }

    private sealed class MinecraftLoginResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; init; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
    }

    private sealed class MinecraftProfile
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
    }
}
